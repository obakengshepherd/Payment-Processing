-- =============================================================================
-- V002__create_payment_events.sql
-- Payment Processing System — payment_events (immutable audit log)
--
-- ROLLBACK:
--   DROP TABLE IF EXISTS payment_events CASCADE;
-- =============================================================================

CREATE TABLE payment_events (
    id           VARCHAR(36)  NOT NULL,
    payment_id   VARCHAR(36)  NOT NULL,
    event_type   VARCHAR(64)  NOT NULL,
    payload      JSONB        NOT NULL DEFAULT '{}',
    occurred_at  TIMESTAMPTZ  NOT NULL DEFAULT NOW(),

    CONSTRAINT payment_events_pkey PRIMARY KEY (id),

    CONSTRAINT payment_events_payment_fk
        FOREIGN KEY (payment_id) REFERENCES payments (id)
        ON DELETE RESTRICT
);

COMMENT ON TABLE payment_events IS
    'Append-only immutable event log. Every state transition writes one row. '
    'The complete lifecycle of any payment is reconstructible from this table alone. '
    'Also published to Kafka topic payments.events after each insert.';

-- =============================================================================
-- V003__create_authorisations_captures_refunds.sql
-- Payment Processing System — authorisations, captures, refunds
--
-- ROLLBACK:
--   DROP TABLE IF EXISTS refunds CASCADE;
--   DROP TABLE IF EXISTS captures CASCADE;
--   DROP TABLE IF EXISTS authorisations CASCADE;
-- =============================================================================

CREATE TABLE authorisations (
    id                 VARCHAR(36)    NOT NULL,
    payment_id         VARCHAR(36)    NOT NULL,
    auth_code          VARCHAR(64)    NOT NULL,
    authorised_amount  DECIMAL(19, 4) NOT NULL,
    expires_at         TIMESTAMPTZ    NOT NULL,
    status             auth_status    NOT NULL DEFAULT 'active',
    created_at         TIMESTAMPTZ    NOT NULL DEFAULT NOW(),

    CONSTRAINT authorisations_pkey PRIMARY KEY (id),

    -- One authorisation per payment — enforced at DB level.
    -- Prevents double-authorisation bugs from creating multiple auth records.
    CONSTRAINT authorisations_payment_unique UNIQUE (payment_id),

    CONSTRAINT authorisations_payment_fk
        FOREIGN KEY (payment_id) REFERENCES payments (id)
        ON DELETE RESTRICT,

    CONSTRAINT authorisations_amount_positive CHECK (authorised_amount > 0)
);

COMMENT ON COLUMN authorisations.expires_at IS
    'After this timestamp, CaptureService rejects capture attempts. '
    'A scheduled job transitions payment status to authorisation_expired '
    'and updates auth status to expired when this time passes.';

CREATE TABLE captures (
    id                 VARCHAR(36)    NOT NULL,
    authorisation_id   VARCHAR(36)    NOT NULL,
    captured_amount    DECIMAL(19, 4) NOT NULL,
    captured_at        TIMESTAMPTZ    NOT NULL DEFAULT NOW(),

    CONSTRAINT captures_pkey PRIMARY KEY (id),

    -- One capture per authorisation — prevents double-capture at DB level.
    -- The application validates this too, but the UNIQUE constraint is the
    -- authoritative guard: a duplicate capture INSERT raises a constraint
    -- violation rather than silently creating a second capture record.
    CONSTRAINT captures_authorisation_unique UNIQUE (authorisation_id),

    CONSTRAINT captures_authorisation_fk
        FOREIGN KEY (authorisation_id) REFERENCES authorisations (id)
        ON DELETE RESTRICT,

    CONSTRAINT captures_amount_positive CHECK (captured_amount > 0)
);

COMMENT ON COLUMN captures.authorisation_id IS
    'UNIQUE constraint: one capture per authorisation. '
    'Database-level guard against double-capture regardless of application state.';

CREATE TABLE refunds (
    id                VARCHAR(36)    NOT NULL,
    payment_id        VARCHAR(36)    NOT NULL,
    amount            DECIMAL(19, 4) NOT NULL,
    reason            VARCHAR(256)   NULL,
    status            refund_status  NOT NULL DEFAULT 'completed',
    idempotency_key   VARCHAR(36)    NOT NULL,
    created_at        TIMESTAMPTZ    NOT NULL DEFAULT NOW(),

    CONSTRAINT refunds_pkey PRIMARY KEY (id),

    CONSTRAINT refunds_payment_fk
        FOREIGN KEY (payment_id) REFERENCES payments (id)
        ON DELETE RESTRICT,

    CONSTRAINT refunds_idempotency_key_unique UNIQUE (idempotency_key),
    -- Prevents duplicate refund processing — same protection as payments.

    CONSTRAINT refunds_amount_positive CHECK (amount > 0)
);

-- =============================================================================
-- V004__add_constraint_verification.sql
-- Payment Processing — Schema validation pass
-- =============================================================================

DO $$
BEGIN
    ASSERT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'merchants'), 'merchants missing';
    ASSERT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'payments'), 'payments missing';
    ASSERT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'payment_events'), 'payment_events missing';
    ASSERT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'authorisations'), 'authorisations missing';
    ASSERT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'captures'), 'captures missing';
    ASSERT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'refunds'), 'refunds missing';
    RAISE NOTICE 'Payment Processing schema verified.';
END;
$$;

-- =============================================================================
-- V005__add_indexes.sql
-- Payment Processing System — All performance indexes
--
-- ROLLBACK (reverse order):
--   DROP INDEX IF EXISTS authorisations_expires_active_idx;
--   DROP INDEX IF EXISTS payment_events_payment_id_idx;
--   DROP INDEX IF EXISTS payments_status_idx;
--   DROP INDEX IF EXISTS payments_merchant_created_idx;
-- =============================================================================

-- Query: Merchant dashboard — "My payments, newest first"
CREATE INDEX payments_merchant_created_idx
    ON payments (merchant_id, created_at DESC);

-- Query: Settlement job — "Find all CAPTURED payments for settlement processing"
CREATE INDEX payments_status_idx
    ON payments (status);

COMMENT ON INDEX payments_status_idx IS
    'Settlement consumer: SELECT ... FROM payments WHERE status = ''captured''. '
    'Also supports operational queries by status.';

-- Query: Fetch event history for a payment in order
CREATE INDEX payment_events_payment_id_idx
    ON payment_events (payment_id, occurred_at ASC);

COMMENT ON INDEX payment_events_payment_id_idx IS
    'GET /payments/{id}/events — full event history in chronological order.';

-- Query: Scheduled job — find active authorisations that have expired
-- Partial index: only active authorisations (the minority)
CREATE INDEX authorisations_expires_active_idx
    ON authorisations (expires_at ASC)
    WHERE status = 'active';

COMMENT ON INDEX authorisations_expires_active_idx IS
    'Partial index: only active authorisations. '
    'Scheduled expiry job: SELECT ... WHERE status=active AND expires_at < NOW(). '
    'Dramatically smaller than a full index since most authorisations are not active.';

-- Query: Fetch all refunds for a payment
CREATE INDEX refunds_payment_id_idx
    ON refunds (payment_id, created_at DESC);

ANALYZE merchants;
ANALYZE payments;
ANALYZE payment_events;
ANALYZE authorisations;
ANALYZE captures;
ANALYZE refunds;
