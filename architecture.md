# Architecture — Payment Processing System

---

## Overview

The Payment Processing System models the lifecycle of a financial transaction through its
three distinct phases: authorisation, capture, and settlement. It is designed around a strict
state machine and an immutable event log. Every state transition is idempotent, every step is
recorded, and the system is built to recover cleanly from partial failures at any point in the
lifecycle. Settlement is handled asynchronously through a Kafka queue, decoupling the
customer-facing payment confirmation from the back-office settlement process.

---

## Architecture Diagram

```
┌─────────────────────────────────────────────────────────────┐
│                  Clients (Merchant Frontends)               │
└──────────────────────────────┬──────────────────────────────┘
                               │ HTTPS
┌──────────────────────────────▼──────────────────────────────┐
│                      Load Balancer                          │
│           (Round-Robin, TLS Termination, mTLS option)       │
└──────────────────────────────┬──────────────────────────────┘
                               │ HTTP
┌──────────────────────────────▼──────────────────────────────┐
│                      Payment API                            │
│   (Auth · Idempotency Middleware · Request Validation)      │
└────────┬──────────────────────────────────┬─────────────────┘
         │                                  │
┌────────▼──────────────┐     ┌─────────────▼────────────────┐
│  AuthorisationService │     │       CaptureService          │
│  (create, validate,   │     │  (capture, partial capture,  │
│   expiry check)       │     │   expiry enforcement)         │
└────────┬──────────────┘     └─────────────┬────────────────┘
         │                                  │
┌────────▼──────────────────────────────────▼────────────────┐
│                       PostgreSQL                            │
│    (payments · authorisations · captures · refunds ·       │
│     payment_events · merchants)                            │
└──────────────────────────────┬─────────────────────────────┘
                               │
               ┌───────────────┴────────────────┐
               │                                │
┌──────────────▼───────────┐    ┌───────────────▼────────────┐
│   Kafka: payments.events  │    │    SettlementService        │
│  (all payment lifecycle   │    │   (async, from Kafka)       │
│   events, audit stream)   │    └────────────────────────────┘
└───────────────────────────┘
```

---

## Layer-by-Layer Description

### Load Balancer

The load balancer terminates TLS for standard merchant API integrations and optionally
supports mTLS for high-value merchant partners who require mutual authentication. Traffic
is distributed round-robin — the Payment API is fully stateless. Health check interval is
10 seconds; instances are removed from rotation after three consecutive failures.

### Payment API

The Payment API is thin by design. It handles three concerns before delegating to the
service layer: merchant authentication (API key validation), request idempotency (checking
`X-Idempotency-Key` against a Redis store to return cached responses for duplicate
requests), and input validation (amounts, field presence, state transition validity at the
API boundary). All business logic lives in the service layer.

The API is versioned at `/api/v1/`. Every endpoint returns a standard envelope:
`{ data: {...}, meta: { request_id, timestamp } }` on success, and
`{ error: { code, message, details } }` on failure.

### Authorisation Service

AuthorisationService handles the first phase of the payment lifecycle. When a payment is
created, it performs the following:

1. Generates a payment ID and records the payment in PENDING state.
2. Calls the simulated external authorisation processor (a stub in this implementation).
3. On authorisation success: records the authorisation with `authorized_amount`, `auth_code`,
   and `expires_at` (24 hours from creation by default); transitions payment to AUTHORISED.
4. Publishes a `PaymentAuthorised` event to Kafka.

The simulated processor call is designed to be replaceable with a real processor integration.
The service layer is agnostic to the processor — it only cares about the authorisation result.

### Capture Service

CaptureService handles the second phase. It:

1. Validates that the payment is in AUTHORISED state.
2. Validates that the authorisation has not expired (`expires_at > now()`).
3. Validates that the capture amount does not exceed the authorised amount.
4. Records the capture, transitions the payment to CAPTURED.
5. Publishes a `PaymentCaptured` event to the `payments.events` Kafka topic, which the
   SettlementService consumes asynchronously.

Partial captures are supported: a merchant may capture less than the authorised amount. In
this implementation, one capture per authorisation is permitted. Multi-capture (capturing
in instalments) is documented as a future enhancement.

### Settlement Service

SettlementService is a Kafka consumer that processes `PaymentCaptured` events. It represents
the back-office settlement step: finalising the transfer of funds to the merchant. In this
implementation, settlement is simulated (stub processor call). The service:

1. Consumes `PaymentCaptured` events from Kafka.
2. Records the settlement attempt.
3. On success: transitions payment to SETTLED, records settlement timestamp.
4. On failure: retries with exponential backoff; after max retries, transitions to SETTLEMENT_FAILED
   and creates an alert.

Settlement runs asynchronously and does not block the capture response to the merchant.
The merchant receives a `PaymentCaptured` confirmation; settlement happens in the background.

### Payment Event Log

Every state transition on every payment is recorded as an immutable entry in the
`payment_events` table. The event log is the authoritative history of a payment. In addition
to the PostgreSQL event table, each event is also published to the `payments.events` Kafka
topic for downstream consumers (fraud detection, merchant webhooks, analytics).

### Cache — Redis (Idempotency Only)

Redis is used exclusively for idempotency key storage in this system. When the API receives
a request with an `X-Idempotency-Key`, it checks Redis using `SETNX`. If the key already
exists, the cached response is returned immediately without executing the operation. If the
key does not exist, the operation proceeds, and the result is cached under the key with a
24-hour TTL.

Unlike the Wallet system, the Payment Processing system does not cache business data in Redis —
all payment state is served directly from PostgreSQL, which is acceptable given the lower
read volume compared to a balance-read-heavy wallet system.

### Database — PostgreSQL

PostgreSQL holds all payment state. The `payments` table contains the current state of each
payment. The `payment_events` table is an append-only log. The `authorisations`, `captures`,
and `refunds` tables hold the detailed records for each lifecycle step. Foreign key constraints
enforce referential integrity between these tables. The `merchants` table holds API key hashes
and webhook configuration.

---

## Component Responsibilities Summary

| Component              | Responsibility                                            | Communicates Via       |
|------------------------|-----------------------------------------------------------|------------------------|
| Load Balancer          | TLS termination, routing, health checks                   | HTTPS                  |
| Payment API            | Auth, idempotency, validation, routing                    | HTTP (internal)        |
| AuthorisationService   | Payment creation, auth phase, expiry management           | PostgreSQL + Kafka     |
| CaptureService         | Capture validation, state transition, settlement trigger  | PostgreSQL + Kafka     |
| RefundService          | Refund validation, state transition, amount check         | PostgreSQL + Kafka     |
| SettlementService      | Async settlement from Kafka consumer                      | Kafka + PostgreSQL     |
| Redis                  | Idempotency key store                                     | In-memory              |
| PostgreSQL             | All payment state, event log, merchant data               | TCP                    |
| Kafka                  | Payment lifecycle event stream                            | Kafka protocol         |
