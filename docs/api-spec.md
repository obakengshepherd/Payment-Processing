# API Specification — Payment Processing System

---

## Overview

The Payment Processing API manages the full lifecycle of a financial payment: creation,
authorisation, capture, refund, and cancellation. It is consumed by merchant frontends,
e-commerce platforms, and internal systems. Every write operation is idempotency-safe.
The state machine is strictly enforced — no transition can be performed out of sequence.
All payment amounts are handled as strings to avoid floating-point imprecision.

---

## Base URL and Versioning

```
https://api.payments.internal/api/v1
```

---

## Authentication

Merchants authenticate using an API key in the `Authorization` header:

```
Authorization: ApiKey <merchant_api_key>
```

Internal service-to-service calls (e.g. Fraud Decision consumer) use Bearer tokens:

```
Authorization: Bearer <service_jwt>
```

---

## Common Response Envelope

### Success
```json
{
  "data": { ... },
  "meta": { "request_id": "uuid", "timestamp": "2024-01-15T10:30:00Z" }
}
```

### Error
```json
{
  "error": {
    "code": "AUTHORISATION_EXPIRED",
    "message": "The authorisation window has expired. A new payment must be created.",
    "details": []
  },
  "meta": { "request_id": "uuid", "timestamp": "2024-01-15T10:30:00Z" }
}
```

---

## Rate Limiting

| Endpoint               | Limit              | Scope          |
|-----------------------|--------------------|----------------|
| `POST /payments`       | 200 / minute       | Per API key    |
| `POST /payments/{id}/capture` | 200 / minute | Per API key  |
| All other endpoints    | 60 / minute        | Per API key    |

---

## Idempotency

All `POST` endpoints require:

```
X-Idempotency-Key: <uuid-v4>
```

A duplicate request within 24 hours returns the original response with **200 OK** (not 201).
The `X-Idempotency-Replayed: true` header is included on replayed responses.

---

## Endpoints

---

### POST /payments

**Description:** Creates a new payment and performs authorisation against the simulated
processor. Returns the payment record with status `AUTHORISED` on success.

**Request Headers:** `X-Idempotency-Key: <uuid-v4>` *(required)*

**Request Body:**

| Field            | Type    | Required | Validation             | Example              |
|------------------|---------|----------|------------------------|----------------------|
| `amount`         | string  | Yes      | Decimal > 0, max 2 dp  | `"1500.00"`          |
| `currency`       | string  | Yes      | ISO 4217, 3 chars      | `"ZAR"`              |
| `customer_ref`   | string  | Yes      | max 128 chars          | `"cust_abc123"`      |
| `description`    | string  | No       | max 256 chars          | `"Order #4821"`      |
| `metadata`       | object  | No       | max 10 keys            | `{"order_id":"4821"}`|

**Response — 201 Created:**
```json
{
  "data": {
    "id": "pay_01j9z3k4m5n6p7q8",
    "amount": "1500.00",
    "currency": "ZAR",
    "status": "AUTHORISED",
    "customer_ref": "cust_abc123",
    "authorisation": {
      "auth_code": "AUTH_XYZ789",
      "authorised_amount": "1500.00",
      "expires_at": "2024-01-16T10:30:00Z"
    },
    "created_at": "2024-01-15T10:30:00Z"
  },
  "meta": { ... }
}
```

**Status Codes:**

| Code | Condition                                         |
|------|---------------------------------------------------|
| 201  | Payment created and authorised                    |
| 400  | Missing/invalid fields                            |
| 401  | Invalid API key                                   |
| 409  | Duplicate idempotency key (returns original)      |
| 422  | Processor declined the authorisation              |
| 500  | Internal server error or processor unavailable    |

---

### POST /payments/{id}/capture

**Description:** Captures an authorised payment. Amount must not exceed the authorised
amount. Authorisation must not be expired.

**Path Parameters:** `id` — Payment ID

**Request Headers:** `X-Idempotency-Key: <uuid-v4>` *(required)*

**Request Body:**

| Field    | Type   | Required | Validation                         | Example      |
|----------|--------|----------|------------------------------------|--------------|
| `amount` | string | Yes      | > 0 and <= authorised_amount       | `"1500.00"`  |

**Response — 200 OK:**
```json
{
  "data": {
    "id": "pay_01j9z3k4m5n6p7q8",
    "status": "CAPTURED",
    "captured_amount": "1500.00",
    "captured_at": "2024-01-15T10:45:00Z"
  },
  "meta": { ... }
}
```

**Status Codes:**

| Code | Condition                                        |
|------|--------------------------------------------------|
| 200  | Payment captured                                 |
| 400  | Invalid amount                                   |
| 401  | Unauthorized                                     |
| 404  | Payment not found                                |
| 409  | Duplicate idempotency key                        |
| 422  | Payment not in AUTHORISED status                 |
| 422  | Authorisation has expired                        |
| 422  | Capture amount exceeds authorised amount         |

---

### POST /payments/{id}/refund

**Description:** Refunds a captured or settled payment. Supports full and partial refunds.
Total refunded amount must not exceed total captured amount.

**Path Parameters:** `id` — Payment ID

**Request Headers:** `X-Idempotency-Key: <uuid-v4>` *(required)*

**Request Body:**

| Field    | Type   | Required | Validation                          | Example     |
|----------|--------|----------|-------------------------------------|-------------|
| `amount` | string | Yes      | > 0 and <= captured_amount          | `"500.00"`  |
| `reason` | string | No       | max 256 chars                       | `"Customer request"` |

**Response — 200 OK:**
```json
{
  "data": {
    "id": "pay_01j9z3k4m5n6p7q8",
    "status": "PARTIALLY_REFUNDED",
    "refund": {
      "refund_id": "ref_01j9z3k4m5n6p7q8",
      "amount": "500.00",
      "reason": "Customer request",
      "refunded_at": "2024-01-15T11:00:00Z"
    }
  },
  "meta": { ... }
}
```

**Status Codes:**

| Code | Condition                                          |
|------|----------------------------------------------------|
| 200  | Refund processed                                   |
| 400  | Invalid amount                                     |
| 401  | Unauthorized                                       |
| 404  | Payment not found                                  |
| 422  | Payment not in CAPTURED or SETTLED status          |
| 422  | Refund amount exceeds remaining refundable amount  |

---

### GET /payments/{id}

**Description:** Returns the full details and current status of a payment.

**Path Parameters:** `id` — Payment ID

**Response — 200 OK:**
```json
{
  "data": {
    "id": "pay_01j9z3k4m5n6p7q8",
    "amount": "1500.00",
    "currency": "ZAR",
    "status": "CAPTURED",
    "customer_ref": "cust_abc123",
    "description": "Order #4821",
    "authorisation": {
      "auth_code": "AUTH_XYZ789",
      "authorised_amount": "1500.00",
      "expires_at": "2024-01-16T10:30:00Z"
    },
    "capture": {
      "captured_amount": "1500.00",
      "captured_at": "2024-01-15T10:45:00Z"
    },
    "refunds": [],
    "created_at": "2024-01-15T10:30:00Z",
    "updated_at": "2024-01-15T10:45:00Z"
  },
  "meta": { ... }
}
```

**Status Codes:**

| Code | Condition              |
|------|------------------------|
| 200  | Success                |
| 401  | Unauthorized           |
| 404  | Payment not found      |

---

### GET /payments/{id}/events

**Description:** Returns the full immutable event history for a payment, ordered
chronologically ascending.

**Path Parameters:** `id` — Payment ID

**Response — 200 OK:**
```json
{
  "data": [
    {
      "id": "evt_01j9z3k4m5n6p7q8",
      "payment_id": "pay_01j9z3k4m5n6p7q8",
      "event_type": "PaymentAuthorised",
      "payload": { "auth_code": "AUTH_XYZ789", "amount": "1500.00" },
      "occurred_at": "2024-01-15T10:30:00Z"
    },
    {
      "id": "evt_02j9z3k4m5n6p7q9",
      "payment_id": "pay_01j9z3k4m5n6p7q8",
      "event_type": "PaymentCaptured",
      "payload": { "captured_amount": "1500.00" },
      "occurred_at": "2024-01-15T10:45:00Z"
    }
  ],
  "meta": { ... }
}
```

**Status Codes:**

| Code | Condition              |
|------|------------------------|
| 200  | Success                |
| 401  | Unauthorized           |
| 404  | Payment not found      |

---

### POST /payments/{id}/cancel

**Description:** Cancels a payment that has been authorised but not yet captured.
Cannot cancel captured or settled payments (use refund instead).

**Path Parameters:** `id` — Payment ID

**Request Body:** *(empty)*

**Response — 200 OK:**
```json
{
  "data": {
    "id": "pay_01j9z3k4m5n6p7q8",
    "status": "CANCELLED",
    "cancelled_at": "2024-01-15T10:35:00Z"
  },
  "meta": { ... }
}
```

**Status Codes:**

| Code | Condition                                      |
|------|------------------------------------------------|
| 200  | Payment cancelled                              |
| 401  | Unauthorized                                   |
| 404  | Payment not found                              |
| 422  | Payment is not in AUTHORISED status            |
