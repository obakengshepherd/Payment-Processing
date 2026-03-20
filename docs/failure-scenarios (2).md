# Failure Scenarios — Payment Processing System

> **Status**: Complete — Days 25–27 implementation. Replaces Phase 1 skeleton.

---

## Scenario 1 — Duplicate Payment Capture (Merchant Retry After Timeout)

**Trigger**
A merchant submits `POST /payments/{id}/capture` and the response is lost due to
a network timeout. They retry — potentially multiple times.

**Affected Components**
CaptureService, `captures` table UNIQUE constraint, Redis idempotency cache.

**User-Visible Impact**
Without mitigation: the customer is charged twice. The merchant's accounting
shows double the expected revenue. Both parties are harmed.

**System Behaviour Without Mitigation**
Both capture requests reach `CaptureService`. Both check `authorisation.status == 'active'`
(second request sees `active` because the first has not committed). Both insert
into `captures`. Customer is double-charged.

**Mitigation**

1. **Database UNIQUE constraint on authorisation_id:** The `captures` table has
   `UNIQUE (authorisation_id)`. The second INSERT raises a unique constraint
   violation. `CaptureService` catches `NpgsqlException` with SqlState `23505`
   and returns the result of the first capture.

2. **Redis idempotency fast path:** The `X-Idempotency-Key` header on the capture
   request is stored in Redis (`SET NX idempotency:capture:{merchantId}:{key} EX 86400`).
   Duplicate requests within 24 hours return the cached response without touching
   the database.

3. **Payment state machine prevents double capture:** The `payments.status` column
   transitions from `authorised` to `captured`. A second capture attempt finds
   `status = 'captured'` and throws `InvalidStateTransitionException` → 422.

**Detection**
- Metric: `idempotency_capture_hits_total` — high retry traffic from a single merchant.
- Alert: `capture_constraint_violations_total > 0` → application-level idempotency
  failed to prevent a DB-level duplicate attempt.

---

## Scenario 2 — Authorisation Expires Before Capture

**Trigger**
A payment is authorised (merchant receives auth code). The merchant's integration
delays or fails to submit a capture within the 24-hour window. The authorisation
expires. The merchant then submits a capture.

**Affected Components**
CaptureService, `authorisations.expires_at`, scheduled expiry job.

**User-Visible Impact**
The merchant receives a 422 Unprocessable Entity: "Authorisation has expired."
The customer's payment hold is released from their account. The merchant must
re-initiate the payment.

**Mitigation**

1. **Expiry enforcement in CaptureService:**
   ```csharp
   if (payment.Auth.ExpiresAt < DateTimeOffset.UtcNow)
       throw new AuthorisationExpiredException(paymentId, payment.Auth.ExpiresAt);
   ```

2. **Proactive expiry notification:** A scheduled job runs every hour and queries:
   ```sql
   SELECT payment_id FROM authorisations
   WHERE status = 'active' AND expires_at < NOW() + INTERVAL '2 hours'
   ```
   For each result, a `PaymentExpiryWarning` event is published to `payments.events`.
   The webhook dispatcher sends the merchant a notification 2 hours before expiry.

3. **Automatic status transition:** The same scheduled job transitions overdue
   authorisations:
   ```sql
   UPDATE authorisations SET status = 'expired' WHERE expires_at < NOW() AND status = 'active';
   UPDATE payments SET status = 'authorisation_expired' WHERE ...;
   ```

4. **Partial index on `authorisations.expires_at WHERE status = 'active'`:** Makes
   the scheduler query O(log N) on the small active subset.

**Detection**
- Metric: `authorisation_expiry_total` counter.
- Alert: `merchant_expiry_warning_delivery_failure_total > 0` → webhook endpoint
  unreachable.

---

## Scenario 3 — External Processor Timeout During Authorisation

**Trigger**
`POST /payments` triggers an `AuthoriseAsync` call to the external payment processor.
The processor does not respond within the configured 5-second timeout.

**Affected Components**
AuthorisationService, external processor, payment record in PENDING state.

**User-Visible Impact**
The merchant receives a 503 with `Retry-After: 5`. The payment record exists in
the database with `status = 'pending'`. The merchant can safely retry using the
same `X-Idempotency-Key`.

**System Behaviour Without Mitigation**
If no timeout is configured, the request thread blocks indefinitely until the
processor responds (or the HTTP client's default timeout triggers). Under load,
all threads block and the API becomes unresponsive.

**Mitigation**

1. **Hard timeout via CancellationToken:**
   ```csharp
   using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
   using var linked    = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
   var authResult = await _processor.AuthoriseAsync(paymentId, amount, currency, linked.Token);
   ```

2. **Circuit breaker on processor calls:** After 5 timeouts or connection failures
   in 30 seconds, the processor circuit opens. All subsequent requests immediately
   receive a 503 without waiting 5 seconds. This preserves thread pool capacity.

3. **Payment marked FAILED on timeout:** `PaymentService` catches
   `OperationCanceledException` from the timeout and sets the payment to `failed`.
   The merchant retries with the same idempotency key → the Redis cache returns
   the FAILED result immediately (no duplicate attempt).

4. **Retry guidance in response:**
   ```json
   { "error": { "code": "PROCESSOR_TIMEOUT",
     "message": "Payment processor did not respond. Retry using the same idempotency key." },
     "retry_after": 5 }
   ```

**Detection**
- Alert: `processor_timeout_rate > 5%` sustained → processor degraded.
- Alert: Circuit breaker opens on processor → immediate page.
- Metric: `processor_call_duration_p99` — alert if > 4s (approaching timeout).

---

## Scenario 4 — Settlement Consumer Falls Behind (Kafka Lag)

**Trigger**
Settlement volume spikes (end of business day). The SettlementService consumer
cannot process `payments.settlement` events as fast as they arrive. Kafka topic
lag grows.

**Affected Components**
SettlementKafkaConsumer, `payments.settlement` topic, merchant funds flow.

**User-Visible Impact**
Captured payments are not settled to merchant accounts within the expected
24-hour SLA. Merchants do not see funds arrive on time.

**System Behaviour Without Mitigation**
Lag grows indefinitely. Settlements fall behind by hours. Merchants escalate.

**Mitigation**

1. **Scale settlement consumers up to partition count (4):** Add instances when
   lag > 5,000 messages. Each additional instance processes one partition in
   parallel.

2. **Retry with exponential backoff (5 attempts):** Each settlement call retries
   up to 5 times with 1s → 2s → 4s → 8s → 16s backoff before writing to the
   dead letter table. This handles transient processor failures without blocking.

3. **Dead letter to PostgreSQL:** Failed settlement messages are written to
   `payment_events` with `event_type = 'SettlementFailed'` for manual resolution.
   Operations team can replay individual settlements via the admin API.

4. **24-hour SLA monitoring:** Alert if any `captured` payment is more than
   23 hours old with no corresponding `PaymentSettled` event.

**Detection**
- Alert: `kafka_consumer_group_lag{group="payment-settlement-consumer"} > 5000`.
- Alert: `payment_settlement_age_max_hours > 23`.
- Metric: `settlement_processing_duration_p99` per batch.

---

## Scenario 5 — PostgreSQL Primary Outage Mid-Payment Lifecycle

**Trigger**
PostgreSQL primary fails between the capture INSERT and the `PaymentCaptured` event
publish to Kafka. Or: fails while a payment state transition is in progress.

**Affected Components**
All write paths, payment state integrity.

**User-Visible Impact**
In-flight write operations return 503. The payment record may be in an intermediate
state (e.g., authorised but capture failed to write; or captured but settlement event
not written).

**Mitigation**

1. **Database transactions prevent partial writes:** Each lifecycle step (authorise,
   capture, refund) wraps its DB writes in a transaction. A primary failure mid-transaction
   causes the transaction to roll back. No partial state is committed.

2. **Idempotent retry on recovery:** After the primary recovers, the merchant retries
   with the same idempotency key. The Redis cache returns the pre-failure state
   (e.g., `pending`) and the merchant knows to re-attempt the capture.

3. **Reconciliation job on recovery:** After primary recovery, a job runs:
   ```sql
   SELECT id FROM payments
   WHERE status = 'authorised' AND updated_at < NOW() - INTERVAL '5 minutes'
   ```
   These payments may have had their capture aborted. The job notifies merchants
   and logs the orphaned records for review.

4. **Circuit breaker on PostgreSQL:** After 5 consecutive connection failures,
   the circuit opens, returning 503 immediately. API stays responsive (no thread
   exhaustion) during the outage window.

**Detection**
- Alert: PostgreSQL health check → Unhealthy.
- Alert: `orphaned_payment_count` from reconciliation job > 0.
- Alert: `payment_write_error_rate > 1%`.

---

## Universal Scenarios

### U1 — Kafka Consumer Lag (covered in Scenario 4)

### U2 — Database Connection Pool Exhaustion
**Impact:** Payment writes queue. CaptureService returns 503 after circuit opens.
Merchants receive `Retry-After` headers. No payment corruption — the idempotency
layer ensures safe retries.

### U3 — Downstream Processor Timeout (covered in Scenario 3)
