# Scaling Strategy — Payment Processing System

---

## Horizontal Scaling Table

| Component               | Scales Horizontally? | Notes                                                     |
|-------------------------|---------------------|-----------------------------------------------------------|
| Payment API             | ✅ Yes               | Stateless; round-robin; no session affinity               |
| AuthorisationService    | ✅ Yes               | In-process; scales with API instances                     |
| CaptureService          | ✅ Yes               | Stateless; scales with API instances                      |
| SettlementService       | ✅ Yes               | Kafka consumer; scale up to partition count (4)           |
| Redis (idempotency)     | ✅ Yes (Cluster)     | Shard by payment_id hash                                  |
| Kafka (payments.events) | ✅ Yes               | Add brokers + partitions for more throughput              |
| PostgreSQL primary      | ❌ No (writes)       | Single primary; scale reads with replicas                 |
| PostgreSQL replicas     | ✅ Yes               | Route display reads and event history here                |
| Merchants table         | ✅ Read cache        | Low-write reference data; can be cached in Redis          |

---

## Load Balancing Configuration

**API Layer (stateless — all instances identical):**
```
Algorithm:   Round-Robin
Affinity:    None — merchant API keys are validated per-request from DB/cache
Health:      GET /health every 10s; 3 failures = remove; 2 successes = restore
mTLS:        Optional for high-value merchant integrations (configured at LB)
```

**Settlement Consumer (not load balanced — Kafka partitions distribute):**
```
Distribution: Kafka consumer group partitioning (not HTTP load balancing)
Max instances: 4 (matches payments.settlement partition count)
Scale trigger: Consumer lag > 5,000 messages
```

---

## Stateless Design Guarantees

1. **No per-instance payment state.** All payment status, authorisation details,
   and event history live in PostgreSQL. Any instance can serve any request.

2. **Idempotency is shared via Redis.** A merchant retrying `POST /payments`
   to a different instance hits the same Redis key and gets the cached response.

3. **Merchant authentication is stateless.** API key hash is in PostgreSQL;
   no per-instance session store.

4. **Event sourcing from PostgreSQL.** The `payment_events` table is the
   complete history. Any instance can reconstruct payment state from events.

---

## Scaling Triggers

| Metric                               | Threshold    | Action                                       |
|--------------------------------------|--------------|----------------------------------------------|
| API p99 latency                      | > 300ms      | Add API instance; review processor latency   |
| Settlement consumer lag              | > 5,000 msgs | Add settlement consumer (max 4)              |
| PostgreSQL write IOPS                | > 70%        | Upgrade storage; review connection pool      |
| Redis memory utilisation             | > 75%        | Increase Redis memory                        |
