# Failure Scenarios — Payment Processing System

> **Status**: Skeleton — stubs defined on Day 2. Full mitigations and implementations added on Day 27.

---

## Scenario 1 — Duplicate Payment Capture

**Trigger**: A merchant submits `POST /payments/{id}/capture` and receives a network timeout.
They retry. Without idempotency protection, the payment is captured twice.

**Component that fails**: Network / client retry behaviour.

**Impact**: User-facing / financial — customer is charged twice; merchant receives double funds.

**Mitigation strategy**: TBD Day 27 — involves idempotency key enforcement via Redis SETNX
and the `idempotency_key` unique index on the `captures` table.

---

## Scenario 2 — Authorisation Expires Before Capture

**Trigger**: A payment is authorised but the merchant does not submit a capture within the
24-hour authorisation window. The authorisation expires. The merchant then submits a capture.

**Component that fails**: Merchant integration delay / authorisation lifecycle management.

**Impact**: User-facing — capture is rejected; merchant must re-initiate the payment.

**Mitigation strategy**: TBD Day 27 — involves `expires_at` check in CaptureService,
automatic status transition to AUTHORISATION_EXPIRED via a scheduled job, and merchant
webhook notification before expiry.

---

## Scenario 3 — Settlement Service Kafka Consumer Lag

**Trigger**: The settlement Kafka consumer falls behind the `payments.events` topic.
Captured payments are not settled promptly.

**Component that fails**: SettlementService consumer throughput.

**Impact**: Internal — payments are correctly captured but settlement is delayed.
Merchants may not receive funds in the expected timeframe.

**Mitigation strategy**: TBD Day 27 — involves consumer instance scaling, lag alerting,
and a settlement SLA definition (e.g., settle within 24 hours of capture).

---

## Scenario 4 — External Processor Timeout During Authorisation

**Trigger**: The simulated external processor call during payment creation takes longer
than the configured timeout (e.g., 5 seconds).

**Component that fails**: External processor / network to processor.

**Impact**: User-facing — payment creation fails with a timeout error. The payment may
have been partially recorded internally.

**Mitigation strategy**: TBD Day 27 — involves timeout enforcement in the service layer,
payment status set to FAILED on timeout, retry guidance returned to the client, and
idempotency key allowing safe client retry.

---

## Scenario 5 — PostgreSQL Primary Outage During Payment Lifecycle

**Trigger**: The PostgreSQL primary becomes unavailable mid-lifecycle (e.g., between
capture and settlement event write).

**Component that fails**: PostgreSQL primary.

**Impact**: User-facing — write operations fail. Payments in flight may be left in an
intermediate state (e.g., authorised but not captured, captured but settlement event
not written).

**Mitigation strategy**: TBD Day 27 — involves idempotent re-execution on recovery,
payment event log reconciliation, and a recovery job that identifies payments in terminal
intermediate states and resolves them.
