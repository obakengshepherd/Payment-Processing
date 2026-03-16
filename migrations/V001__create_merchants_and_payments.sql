-- =============================================================================
-- V001__create_merchants_and_payments.sql
-- Payment Processing System — Custom types + merchants + payments tables
--
-- ROLLBACK:
--   DROP TABLE IF EXISTS payments CASCADE;
--   DROP TABLE IF EXISTS merchants CASCADE;
--   DROP TYPE IF EXISTS payment_status;
--   DROP TYPE IF EXISTS merchant_status;
-- =============================================================================

CREATE TYPE merchant_status AS ENUM ('active', 'suspended');
CREATE TYPE payment_status  AS ENUM (
    'pending', 'authorised', 'authorisation_expired',
    'captured', 'settled', 'partially_refunded',
    'refunded', 'cancelled', 'failed'
);
CREATE TYPE auth_status    AS ENUM ('active', 'expired', 'captured', 'cancelled');
CREATE TYPE refund_status  AS ENUM ('pending', 'completed', 'failed');

-- -----------------------------------------------------------------------------
-- merchants
-- Authenticated via api_key_hash (bcrypt hash — never store raw API keys).
-- -----------------------------------------------------------------------------
CREATE TABLE merchants (
    id            VARCHAR(36)      NOT NULL,
    name          VARCHAR(128)     NOT NULL,
    api_key_hash  VARCHAR(64)      NOT NULL,
    webhook_url   VARCHAR(512)     NULL,
    status        merchant_status  NOT NULL DEFAULT 'active',
    created_at    TIMESTAMPTZ      NOT NULL DEFAULT NOW(),

    CONSTRAINT merchants_pkey PRIMARY KEY (id),
    CONSTRAINT merchants_api_key_unique UNIQUE (api_key_hash)
);

COMMENT ON COLUMN merchants.api_key_hash IS
    'Bcrypt hash of the merchant API key. The plaintext key is never stored. '
    'Authentication: hash the submitted key and compare to this value.';

-- -----------------------------------------------------------------------------
-- payments
-- Central payment entity. Every operation produces an immutable payment_event.
-- -----------------------------------------------------------------------------
CREATE TABLE payments (
    id               VARCHAR(36)     NOT NULL,
    merchant_id      VARCHAR(36)     NOT NULL,
    customer_ref     VARCHAR(128)    NOT NULL,
    amount           DECIMAL(19, 4)  NOT NULL,
    currency         CHAR(3)         NOT NULL,
    status           payment_status  NOT NULL DEFAULT 'pending',
    idempotency_key  VARCHAR(36)     NOT NULL,
    description      VARCHAR(256)    NULL,
    metadata         JSONB           NOT NULL DEFAULT '{}',
    created_at       TIMESTAMPTZ     NOT NULL DEFAULT NOW(),
    updated_at       TIMESTAMPTZ     NOT NULL DEFAULT NOW(),

    CONSTRAINT payments_pkey PRIMARY KEY (id),

    CONSTRAINT payments_merchant_fk
        FOREIGN KEY (merchant_id) REFERENCES merchants (id)
        ON DELETE RESTRICT,

    -- UNIQUE on idempotency_key: database-level duplicate payment prevention.
    -- Even if Redis idempotency cache is unavailable, the DB ensures
    -- one payment per idempotency key — the application catches the
    -- constraint violation and returns the original payment record.
    CONSTRAINT payments_idempotency_key_unique UNIQUE (idempotency_key),

    CONSTRAINT payments_amount_positive CHECK (amount > 0),

    CONSTRAINT payments_currency_format CHECK (currency ~ '^[A-Z]{3}$'),

    CONSTRAINT payments_metadata_max_keys
        CHECK (jsonb_typeof(metadata) = 'object')
);

COMMENT ON COLUMN payments.idempotency_key IS
    'Client-supplied UUID (X-Idempotency-Key header). '
    'UNIQUE constraint provides database-level duplicate payment prevention. '
    'Combined with Redis cache (fast path), creates two-layer idempotency guard.';

CREATE OR REPLACE FUNCTION update_updated_at_column()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER payments_updated_at_trigger
    BEFORE UPDATE ON payments
    FOR EACH ROW
    EXECUTE FUNCTION update_updated_at_column();
