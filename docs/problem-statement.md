# Problem Statement — Payment Processing System

---

## Section 1 — The Problem.

A payment gateway sits between a customer's intent to pay and the actual movement of funds.
It must authorise the payment (verify the funds exist and are available), capture it (claim
those funds), and settle it (finalise the transfer to the merchant). Each step in this
lifecycle involves external systems, network calls that can fail, and state that must remain
consistent even when things go wrong. The business cost of failure here is high in both
directions: a failed legitimate payment loses revenue and trust; a payment that is captured
without proper authorisation, or authorised but never settled, creates financial and
operational problems for the merchant.

---

## Section 2 — Why It Is Hard

- **Multi-step lifecycle with partial failure**: Authorisation, capture, and settlement are
  distinct operations, each of which can fail independently. If capture succeeds but settlement
  fails, or if authorisation succeeds but the capture service crashes before recording the
  result, the system must know how to recover to a consistent state rather than leaving
  payments stranded in an ambiguous intermediate state.

- **Idempotency at every step**: Payment APIs are called over unreliable networks. A client
  that does not receive a response will retry. Without idempotency protection at each
  endpoint, a single payment can be authorised or captured multiple times. In a financial
  context this is not an edge case — it is a certainty at scale.

- **State machine integrity**: A payment must follow a valid sequence of states:
  PENDING → AUTHORISED → CAPTURED → SETTLED. Transitions in the wrong order (e.g. capturing
  an already-captured payment, refunding a payment that was never captured) must be rejected
  with a clear error. The state machine must be enforced at the service layer, not just the
  API layer.

- **Authorisation expiry**: An authorisation is a temporary hold on funds — it expires. If
  a capture is not performed before expiry, the funds are released. The system must track
  authorisation expiry and prevent late captures from being accepted silently.

- **Audit trail**: Every state transition must be recorded with a timestamp and cause. Dispute
  resolution, reconciliation, and regulatory compliance all depend on a complete, immutable
  record of what happened to every payment and when.

---

## Section 3 — Scope of This Implementation.

**In scope:**

- Payment creation with idempotency key enforcement
- Authorisation step (simulated external processor call)
- Capture step with partial capture support (capture up to authorised amount)
- Refund processing (full and partial, up to captured amount)
- Payment cancellation (before capture only)
- Payment state machine with enforced valid transitions
- Payment event log: every state change recorded as an immutable event
- Settlement queue: captured payments published to Kafka for async settlement processing
- Merchant API key authentication
- Webhook event publishing stub for merchant notification

**Out of scope:**

- Integration with real card networks or banking APIs
- 3DS / strong customer authentication flows
- Recurring billing or subscription management
- Multi-currency settlement and FX conversion
- Merchant onboarding and KYC
- Chargeback management and dispute workflows

---

## Section 4 — Success Criteria.

The system is working correctly when:

1. No payment can be captured more than once, regardless of how many concurrent capture
   requests are submitted with the same payment ID.

2. Submitting the same `POST /payments` request twice with the same idempotency key creates
   exactly one payment record and returns the same response on both calls.

3. Every payment state transition is preceded by a validation that the transition is legal
   from the current state; illegal transitions return a 422 with a clear error and do not
   modify the payment record.

4. A capture submitted after the authorisation has expired is rejected with an appropriate
   error, and the payment status reflects the expired authorisation.

5. Every state change on every payment is recorded in the `payment_events` table with a
   timestamp accurate to the millisecond, and the full event history can be retrieved for
   any payment.

6. The system processes 200 transactions per second at peak load with a p99 authorisation
   response time under 300ms and zero data loss on the settlement queue.
