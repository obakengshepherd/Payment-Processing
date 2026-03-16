# Data Model — Payment Processing System

---

## Database Technology Choices

### PostgreSQL (All payment state)
Payment data is entirely in PostgreSQL. The rationale is identical to the Digital Wallet
system: ACID transactions, exact decimal arithmetic, and the ability to enforce business
rules via CHECK constraints and enum types at the database level. Unlike the wallet system,
the Payment Processing system does not cache business data in Redis — payment state queries
always reflect committed database state. Stale data is more dangerous than slow data in a
payment context.

### Redis (Idempotency keys only)
Redis stores idempotency key results with a 24-hour TTL. If Redis is unavailable,
idempotency falls back to a unique index on `payments.idempotency_key` in PostgreSQL.
No payment processing is blocked by a Redis outage.

---

## Entity Relationship Overview

A **Merchant** is authenticated by their API key hash. They create **Payments** on behalf
of customers. Each Payment progresses through a state machine and its every transition is
recorded as an immutable **PaymentEvent**.

An **Authorisation** is the record of the processor's approval: it holds the auth code,
the authorised amount, and the expiry. A Payment has at most one Authorisation.

A **Capture** records how much was actually captured from an authorisation. A single
Authorisation has at most one Capture in this implementation.

A **Refund** records money returned to the customer. A Payment can have multiple partial
refunds, up to the total captured amount.

---

## Table Definitions

### `merchants`

| Column          | Type          | Constraints                        | Description                             |
|-----------------|---------------|------------------------------------|-----------------------------------------|
| `id`            | `VARCHAR(36)` | PRIMARY KEY                        | Prefixed UUID: `mrc_<uuid>`             |
| `name`          | `VARCHAR(128)`| NOT NULL                           | Business name                           |
| `api_key_hash`  | `VARCHAR(64)` | NOT NULL, UNIQUE                   | Bcrypt hash of the API key              |
| `webhook_url`   | `VARCHAR(512)`| NULL                               | Endpoint for event notifications        |
| `status`        | `merchant_status`| NOT NULL, DEFAULT 'active'      | Enum: `active`, `suspended`             |
| `created_at`    | `TIMESTAMPTZ` | NOT NULL, DEFAULT NOW()            | Immutable                               |

**Why store `api_key_hash` and not the key itself?** API keys are credentials. Storing
them hashed means a database breach does not expose usable keys. Authentication verifies
by hashing the submitted key and comparing to the stored hash.

### `payments`

| Column            | Type             | Constraints                          | Description                                   |
|-------------------|------------------|--------------------------------------|-----------------------------------------------|
| `id`              | `VARCHAR(36)`    | PRIMARY KEY                          | Prefixed UUID: `pay_<uuid>`                   |
| `merchant_id`     | `VARCHAR(36)`    | NOT NULL, FK → merchants             | Owning merchant                               |
| `customer_ref`    | `VARCHAR(128)`   | NOT NULL                             | Merchant's customer identifier                |
| `amount`          | `DECIMAL(19,4)`  | NOT NULL, CHECK (amount > 0)         | Original payment amount                       |
| `currency`        | `CHAR(3)`        | NOT NULL                             | ISO 4217                                      |
| `status`          | `payment_status` | NOT NULL, DEFAULT 'pending'          | Enum (see below)                              |
| `idempotency_key` | `VARCHAR(36)`    | NOT NULL, UNIQUE                     | Client-supplied UUID — prevents duplicate payments |
| `description`     | `VARCHAR(256)`   | NULL                                 | Optional payment description                  |
| `metadata`        | `JSONB`          | NOT NULL, DEFAULT '{}'               | Merchant-supplied arbitrary key-value data    |
| `created_at`      | `TIMESTAMPTZ`    | NOT NULL, DEFAULT NOW()              | Immutable                                     |
| `updated_at`      | `TIMESTAMPTZ`    | NOT NULL, DEFAULT NOW()              | Updated on every status transition            |

**Payment status enum:** `pending`, `authorised`, `authorisation_expired`, `captured`,
`settled`, `partially_refunded`, `refunded`, `cancelled`, `failed`

### `payment_events`

| Column        | Type          | Constraints              | Description                                      |
|---------------|---------------|--------------------------|--------------------------------------------------|
| `id`          | `VARCHAR(36)` | PRIMARY KEY              | Prefixed UUID: `evt_<uuid>`                      |
| `payment_id`  | `VARCHAR(36)` | NOT NULL, FK → payments  | Parent payment                                   |
| `event_type`  | `VARCHAR(64)` | NOT NULL                 | e.g. `PaymentAuthorised`, `PaymentCaptured`      |
| `payload`     | `JSONB`       | NOT NULL, DEFAULT '{}'   | Event-specific data snapshot                     |
| `occurred_at` | `TIMESTAMPTZ` | NOT NULL, DEFAULT NOW()  | Immutable                                        |

Append-only. Every state transition writes one row. The full event history reconstructs
any payment's lifecycle without relying on current column values.

### `authorisations`

| Column              | Type           | Constraints                       | Description                            |
|---------------------|----------------|-----------------------------------|----------------------------------------|
| `id`                | `VARCHAR(36)`  | PRIMARY KEY                       | Prefixed UUID: `auth_<uuid>`           |
| `payment_id`        | `VARCHAR(36)`  | NOT NULL, UNIQUE, FK → payments   | One authorisation per payment          |
| `auth_code`         | `VARCHAR(64)`  | NOT NULL                          | Code returned by the processor         |
| `authorised_amount` | `DECIMAL(19,4)`| NOT NULL, CHECK (authorised_amount > 0) | Approved amount                  |
| `expires_at`        | `TIMESTAMPTZ`  | NOT NULL                          | After this timestamp, capture fails    |
| `status`            | `auth_status`  | NOT NULL, DEFAULT 'active'        | Enum: `active`, `expired`, `captured`, `cancelled` |
| `created_at`        | `TIMESTAMPTZ`  | NOT NULL, DEFAULT NOW()           | Immutable                              |

### `captures`

| Column            | Type           | Constraints                           | Description                         |
|-------------------|----------------|---------------------------------------|-------------------------------------|
| `id`              | `VARCHAR(36)`  | PRIMARY KEY                           | Prefixed UUID: `cap_<uuid>`         |
| `authorisation_id`| `VARCHAR(36)`  | NOT NULL, UNIQUE, FK → authorisations | One capture per authorisation       |
| `captured_amount` | `DECIMAL(19,4)`| NOT NULL, CHECK (captured_amount > 0) | Actual captured amount              |
| `captured_at`     | `TIMESTAMPTZ`  | NOT NULL, DEFAULT NOW()               | Immutable                           |

### `refunds`

| Column         | Type           | Constraints                        | Description                             |
|----------------|----------------|------------------------------------|-----------------------------------------|
| `id`           | `VARCHAR(36)`  | PRIMARY KEY                        | Prefixed UUID: `ref_<uuid>`             |
| `payment_id`   | `VARCHAR(36)`  | NOT NULL, FK → payments            | Parent payment                          |
| `amount`       | `DECIMAL(19,4)`| NOT NULL, CHECK (amount > 0)       | Refund amount                           |
| `reason`       | `VARCHAR(256)` | NULL                               | Merchant-supplied reason                |
| `status`       | `refund_status`| NOT NULL, DEFAULT 'completed'      | Enum: `pending`, `completed`, `failed`  |
| `idempotency_key`| `VARCHAR(36)`| NOT NULL, UNIQUE                   | Prevent duplicate refund processing     |
| `created_at`   | `TIMESTAMPTZ`  | NOT NULL, DEFAULT NOW()            | Immutable                               |

---

## Index Strategy

| Index Name                              | Table             | Columns                          | Type    | Query Pattern                                  |
|-----------------------------------------|-------------------|----------------------------------|---------|------------------------------------------------|
| `payments_idempotency_key_uniq`         | `payments`        | `(idempotency_key)`              | UNIQUE  | Duplicate payment prevention                   |
| `payments_merchant_created_idx`         | `payments`        | `(merchant_id, created_at DESC)` | B-tree  | Merchant's payment history                     |
| `payments_status_idx`                   | `payments`        | `(status)`                       | B-tree  | Settlement job: find CAPTURED payments         |
| `payment_events_payment_id_idx`         | `payment_events`  | `(payment_id, occurred_at)`      | B-tree  | Fetch event history for a payment              |
| `authorisations_payment_id_uniq`        | `authorisations`  | `(payment_id)`                   | UNIQUE  | One authorisation per payment                  |
| `authorisations_expires_at_idx`         | `authorisations`  | `(expires_at) WHERE status = 'active'` | Partial B-tree | Find expired authorisations via scheduled job |
| `captures_authorisation_id_uniq`        | `captures`        | `(authorisation_id)`             | UNIQUE  | Prevent double-capture at DB level             |
| `refunds_idempotency_key_uniq`          | `refunds`         | `(idempotency_key)`              | UNIQUE  | Prevent duplicate refund processing            |
| `refunds_payment_id_idx`                | `refunds`         | `(payment_id)`                   | B-tree  | Fetch refunds for a payment                    |

---

## Relationship Types

- **Merchant → Payments**: one-to-many.
- **Payment → PaymentEvents**: one-to-many (append-only audit log).
- **Payment → Authorisation**: one-to-one (enforced by UNIQUE on `authorisations.payment_id`).
- **Authorisation → Capture**: one-to-one (enforced by UNIQUE on `captures.authorisation_id`).
- **Payment → Refunds**: one-to-many (multiple partial refunds allowed).

---

## Soft Delete Strategy

Payments are never deleted. Status transitions are permanent. A cancelled payment remains
in the database with `status = 'cancelled'`. This is the regulatory requirement for
financial transaction records.

Merchants are suspended (`status = 'suspended'`), never deleted.

---

## Audit Trail

| Table             | `created_at` | `updated_at` | Notes                                             |
|-------------------|--------------|--------------|---------------------------------------------------|
| `merchants`       | ✓            | ✗            | Static after creation; status changes via flag    |
| `payments`        | ✓            | ✓            | Updated on every status transition                |
| `payment_events`  | `occurred_at`| ✗            | Append-only — the definitive history              |
| `authorisations`  | ✓            | ✗            | Immutable after creation                          |
| `captures`        | `captured_at`| ✗            | Immutable                                         |
| `refunds`         | ✓            | ✗            | Immutable                                         |
