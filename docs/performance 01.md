# Performance — Payment Processing System

---

## Current Bottlenecks

### Bottleneck 1: Synchronous authorisation blocks request thread
`POST /payments` calls the (simulated) external processor synchronously. At 200
TPS, each request holds a thread for the processor round-trip duration. If the
processor slows to 200ms average, throughput is limited to `threads / 0.2s`.

**Short-term mitigation:** Connection pool sized to handle 200 TPS with 300ms processor latency.
**Long-term:** Move to async authorisation (submit then poll) for high-volume merchants.

### Bottleneck 2: Idempotency key lookup on every POST
Every `POST /payments` checks Redis first, then PostgreSQL unique index as fallback.
At 200 TPS that is 200 Redis reads/second — trivial. But on a Redis cold start,
all 200 fall through to PostgreSQL, which must handle an additional 200 reads/second
on top of its write load.

---

## Cache Hit Rate Targets

| Key                            | Target  | Notes                                      |
|-------------------------------|---------|---------------------------------------------|
| `idempotency:payment:{m}:{k}` | ≥ 99%   | Retry-heavy clients benefit most            |
| `payment:status:{id}`          | ≥ 70%   | 5-min TTL; changes multiple times per payment|

---

## Database Read Replica Routing

| Operation                        | Target        | Reason                             |
|----------------------------------|---------------|------------------------------------|
| `GET /payments/{id}`             | Read replica  | Display; 5-min eventual OK         |
| `GET /payments/{id}/events`      | Read replica  | Immutable history; eventual OK     |
| `POST /payments` (idempotency check) | **Primary** | Must see latest insert            |
| All status updates               | **Primary**   | Write path                         |
| Settlement consumer reads        | **Primary**   | Must see captured status           |

---

## Connection Pool Sizing

| Setting               | Value | Rationale                                             |
|-----------------------|-------|-------------------------------------------------------|
| Max pool per instance | 20    | Includes processor I/O wait time                      |
| Settlement consumer   | 5     | Async consumer; holds connection briefly              |

---

## Query Performance Targets

| Query                                        | Target p95 | Index                              |
|---------------------------------------------|-----------|-------------------------------------|
| Idempotency key lookup (`WHERE idempotency_key`) | < 2ms | `payments_idempotency_key_unique`|
| `UPDATE payments SET status`                 | < 5ms     | PK                                 |
| `INSERT payment_events`                      | < 5ms     | Sequential                         |
| `SELECT payments WHERE merchant_id`          | < 15ms    | `payments_merchant_created_idx`    |
| `SELECT payments WHERE status = 'captured'` (settlement)| < 10ms | `payments_status_idx`|

---

## Rate Limiting Configuration

| Policy         | Limit | Window | Endpoint                    |
|----------------|-------|--------|-----------------------------|
| payment-create | 200   | 1 min  | `POST /payments`            |
| authenticated  | 60    | 1 min  | All other endpoints         |
| unauthenticated| 10    | 1 min  | By IP                       |
