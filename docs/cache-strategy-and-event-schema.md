# Cache Strategy — Payment Processing System

---

## Pattern: SETNX (Distributed Mutex for Idempotency)

The payment system's Redis use is focused on idempotency — preventing duplicate
charges under network retries. Unlike the wallet system, payment data is never
cached for read performance. Stale data is more dangerous than slow data when money
is involved.

```
POST /payments {X-Idempotency-Key: key}
  1. GET idempotency:payment:{merchantId}:{key}
  2. HIT  → return cached response, skip all processing
  3. MISS → proceed with payment creation
         → AUTHORISE with processor
         → on success: SET NX idempotency:payment:{merchantId}:{key} {response} EX 86400
```

`SET NX EX` is atomic — no race condition between checking and setting.

---

## Key Inventory

| Key Pattern                                | Type   | TTL  | Written When           |
|-------------------------------------------|--------|------|------------------------|
| `idempotency:payment:{merchantId}:{key}`  | String | 24h  | After successful create|
| `payment:status:{paymentId}`              | String | 5min | After status transition|

---

## Failure Handling

- Redis down during idempotency check → `null` returned → fall back to PostgreSQL
  unique index on `idempotency_key` column as the authoritative guard.
- Redis down during status cache set → logged, not thrown, no impact on correctness.

---

# Event Schema — Payment Processing System

## Topics

### `payments.events`
- **Producer:** PaymentService, CaptureService, SettlementService
- **Partitioned by:** `payment_id` — all events for a payment arrive in order
- **Consumers:** FraudDetection, merchant webhook dispatcher, analytics
- **Retention:** 30 days
- **Partitions:** 8

**Message schema:**
```json
{
  "event_id": "uuid",
  "event_type": "PaymentCaptured",
  "payment_id": "pay_01j9...",
  "occurred_at": "2024-01-15T10:45:00Z",
  "payload": {
    "captured_amount": "1500.00",
    "currency": "ZAR"
  }
}
```

**Event types produced:** `PaymentCreated`, `PaymentAuthorised`, `PaymentCaptured`,
`PaymentSettled`, `PaymentRefunded`, `PaymentCancelled`, `PaymentFailed`

### `payments.settlement`
- **Producer:** CaptureService (on every successful capture)
- **Partitioned by:** `merchant_id` — batches settlement by merchant
- **Consumers:** SettlementService (group: `payment-settlement-consumer`)
- **Retention:** 7 days
- **Partitions:** 4

### Dead Letter Handling

Failed settlement messages (after 5 retries) are written to a `payment_events`
row with `event_type = 'SettlementFailed'` in PostgreSQL. This preserves the
audit trail and enables manual reprocessing by operators.

---

## Consumer Groups

| Consumer Group                    | Topic               | Purpose                          |
|-----------------------------------|---------------------|----------------------------------|
| `payment-settlement-consumer`     | payments.settlement | Async settlement processing      |
| `fraud-engine`                    | payments.events     | Evaluate payments for fraud      |
| `webhook-dispatcher`              | payments.events     | Notify merchants via webhook     |

---

## Slow Consumer Behaviour

`payments.settlement` messages represent committed captures — money that must be
settled. A slow settlement consumer means merchants wait longer for funds, but no
money is lost (messages are retained for 7 days). At lag > 5,000 messages, add
consumer instances and alert the on-call engineer.
