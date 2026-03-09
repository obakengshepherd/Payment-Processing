# Scaling Strategy — Payment Processing System

---

## Current Single-Node Bottlenecks

- **Authorisation step serialisation**: Each payment creation involves a simulated external
  processor call. Even at a stub level, this is a network I/O operation per request. At 200
  TPS, this is manageable — but if the external processor is slow or rate-limited, requests
  back up quickly. Async processing would solve throughput but introduces state management
  complexity.

- **PostgreSQL write volume**: Every payment involves multiple writes — the payment row,
  one or more event rows, an authorisation row. At 200 TPS that is 600–800 database writes
  per second sustained. For a single PostgreSQL primary with NVMe storage, this is within
  capacity, but it is the first component to reach saturation as throughput grows.

- **Payment event log growth**: The `payment_events` table grows at roughly 5 rows per
  payment (authorised, captured, settled, plus metadata). At 50,000 transactions/day that
  is ~250,000 rows/day. Manageable, but table scans and history queries degrade if
  unindexed and unpartitioned.

- **Idempotency Redis dependency**: The idempotency check adds one Redis roundtrip to every
  write request. A Redis failure does not block payment processing (idempotency can degrade
  gracefully to a database check), but it should be monitored.

---

## Horizontal Scaling Plan

### Payment API

The API is fully stateless. Scale horizontally behind the load balancer using round-robin.
The only shared state is the idempotency key store in Redis, which all instances access.
No coordination between instances is needed.

Target: 3 API instances handling 200 TPS steady-state (headroom for peak of 500 TPS).

### Service Layer

AuthorisationService and CaptureService run in-process with the API. They scale with the
API layer. If services are extracted as independent deployments (future architecture), they
scale the same way — stateless, round-robin behind an internal load balancer.

### Settlement Service

SettlementService is a Kafka consumer. It scales up to the partition count of the
`payments.events` topic. Start with 4 partitions and 4 settlement consumer instances.
Settlement does not have a latency SLA (it is async) — it just needs to keep up with
capture throughput over time.

### Redis

A single Redis instance handles idempotency key storage with ease. Each key is a 64-byte
string with a 24-hour TTL. At 200 TPS with a 24-hour TTL, peak key count is
200 × 86,400 = ~17M keys, consuming roughly 2GB of memory. Size Redis accordingly.

If Redis becomes unavailable, degrade idempotency to a synchronous database lookup on the
`payments` table using the `idempotency_key` unique index. This is slower but prevents
payment processing from halting entirely during a Redis outage.

### PostgreSQL

**Phase 1 — Read replicas**: Route payment status queries and event history reads to a
read replica. All writes target the primary.

**Phase 2 — Connection pooling**: Add PgBouncer in front of the primary to pool connections
as API instance count grows.

**Phase 3 — Partition payment_events**: Partition by month once the table exceeds 100M rows.
Historical events can be archived; the current month's partition remains hot and fast.

**Phase 4 — Separate event log**: At very high scale, consider separating the `payment_events`
table into a dedicated append-only store (e.g. Kafka topic with compacted log + cold storage)
to reduce write pressure on the operational database.

---

## Queue Throughput Targets

| Topic              | Direction | Expected Rate | Partition Count | Notes                         |
|--------------------|-----------|---------------|-----------------|-------------------------------|
| `payments.events`  | Produce   | 200 TPS       | 8               | All lifecycle events          |
| `payments.events`  | Consume   | 200 TPS       | 8               | SettlementService consumes    |
| `fraud.decisions`  | Consume   | 200 TPS       | 8               | BLOCK decisions update payment|

Monitor `payments.events` consumer group lag for the SettlementService. Alert at lag > 5,000
messages. Settlement delay is acceptable; settlement backlog growth is not.

---

## Cache Targets

| Cache Key                | TTL   | Purpose                          | On Failure                          |
|--------------------------|-------|----------------------------------|-------------------------------------|
| `idempotency:{key}`      | 24h   | Duplicate request prevention     | Fall back to DB unique index check  |

No business data is cached. Payment status queries always reflect the committed database
state. This simplifies consistency reasoning at the cost of slightly higher read latency —
an acceptable tradeoff for a financial system where stale data is more dangerous than slow data.
