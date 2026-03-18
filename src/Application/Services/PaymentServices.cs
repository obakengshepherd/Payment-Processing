using System.Text.Json;
using Confluent.Kafka;
using Dapper;
using Npgsql;
using PaymentProcessing.Api.Models.Requests;
using PaymentProcessing.Api.Models.Responses;
using PaymentProcessing.Application.Interfaces;

namespace PaymentProcessing.Infrastructure.Persistence;

// ════════════════════════════════════════════════════════════════════════════
// PAYMENT REPOSITORY
// ════════════════════════════════════════════════════════════════════════════

public class PaymentRepository
{
    private readonly string _connectionString;

    public PaymentRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("PostgreSQL")
            ?? throw new InvalidOperationException("PostgreSQL connection string missing.");
    }

    public NpgsqlConnection CreateConnection() => new(_connectionString);

    public async Task<PaymentRecord?> FindByIdAsync(string paymentId)
    {
        using var conn = CreateConnection();
        const string sql = """
            SELECT p.id, p.merchant_id, p.customer_ref, p.amount, p.currency,
                   p.status, p.idempotency_key, p.description, p.metadata,
                   p.created_at, p.updated_at,
                   a.id AS auth_id, a.auth_code, a.authorised_amount, a.expires_at, a.status AS auth_status,
                   c.id AS capture_id, c.captured_amount, c.captured_at,
                   r.id AS refund_id, r.amount AS refund_amount, r.reason, r.created_at AS refunded_at
            FROM payments p
            LEFT JOIN authorisations a ON a.payment_id = p.id
            LEFT JOIN captures c ON c.authorisation_id = a.id
            LEFT JOIN refunds r ON r.payment_id = p.id
            WHERE p.id = @PaymentId
            """;
        // Multi-mapping for joins
        PaymentRecord? payment = null;
        await conn.QueryAsync<PaymentRecord, AuthRecord?, CaptureRecord?, RefundRecord?, PaymentRecord>(
            sql,
            (p, a, c, r) =>
            {
                payment ??= p;
                if (a is not null) payment = payment with { Auth = a };
                if (c is not null) payment = payment with { Capture = c };
                if (r is not null) payment = payment with
                    { Refunds = [.. (payment.Refunds ?? []), r] };
                return payment;
            },
            new { PaymentId = paymentId },
            splitOn: "auth_id,capture_id,refund_id");
        return payment;
    }

    public async Task<PaymentRecord?> FindByIdempotencyKeyAsync(string key)
    {
        using var conn = CreateConnection();
        const string sql = "SELECT * FROM payments WHERE idempotency_key = @Key";
        return await conn.QuerySingleOrDefaultAsync<PaymentRecord>(sql, new { Key = key });
    }

    public async Task InsertPaymentAsync(PaymentRecord payment, NpgsqlConnection conn)
    {
        const string sql = """
            INSERT INTO payments
                (id, merchant_id, customer_ref, amount, currency, status, idempotency_key, description, metadata, created_at, updated_at)
            VALUES
                (@Id, @MerchantId, @CustomerRef, @Amount, @Currency, @Status::payment_status,
                 @IdempotencyKey, @Description, @Metadata::jsonb, @CreatedAt, @UpdatedAt)
            """;
        await conn.ExecuteAsync(sql, payment);
    }

    public async Task UpdateStatusAsync(string paymentId, string newStatus, NpgsqlConnection conn)
    {
        const string sql = """
            UPDATE payments
            SET status = @Status::payment_status, updated_at = NOW()
            WHERE id = @PaymentId
            """;
        await conn.ExecuteAsync(sql, new { PaymentId = paymentId, Status = newStatus });
    }

    public async Task InsertAuthorisationAsync(AuthRecord auth, NpgsqlConnection conn)
    {
        const string sql = """
            INSERT INTO authorisations (id, payment_id, auth_code, authorised_amount, expires_at, status, created_at)
            VALUES (@Id, @PaymentId, @AuthCode, @AuthorisedAmount, @ExpiresAt, @Status::auth_status, @CreatedAt)
            """;
        await conn.ExecuteAsync(sql, auth);
    }

    public async Task InsertCaptureAsync(CaptureRecord capture, NpgsqlConnection conn)
    {
        const string sql = """
            INSERT INTO captures (id, authorisation_id, captured_amount, captured_at)
            VALUES (@Id, @AuthorisationId, @CapturedAmount, @CapturedAt)
            """;
        await conn.ExecuteAsync(sql, capture);
    }

    public async Task InsertRefundAsync(RefundRecord refund, NpgsqlConnection conn)
    {
        const string sql = """
            INSERT INTO refunds (id, payment_id, amount, reason, status, idempotency_key, created_at)
            VALUES (@Id, @PaymentId, @Amount, @Reason, @Status::refund_status, @IdempotencyKey, @CreatedAt)
            """;
        await conn.ExecuteAsync(sql, refund);
    }

    public async Task InsertEventAsync(PaymentEventRecord evt, NpgsqlConnection conn)
    {
        const string sql = """
            INSERT INTO payment_events (id, payment_id, event_type, payload, occurred_at)
            VALUES (@Id, @PaymentId, @EventType, @Payload::jsonb, @OccurredAt)
            """;
        await conn.ExecuteAsync(sql, evt);
    }

    public async Task<IEnumerable<PaymentEventRecord>> GetEventsAsync(string paymentId)
    {
        using var conn = CreateConnection();
        const string sql = """
            SELECT id, payment_id, event_type, payload, occurred_at
            FROM payment_events
            WHERE payment_id = @PaymentId
            ORDER BY occurred_at ASC
            """;
        return await conn.QueryAsync<PaymentEventRecord>(sql, new { PaymentId = paymentId });
    }

    public async Task<decimal> GetTotalRefundedAsync(string paymentId)
    {
        using var conn = CreateConnection();
        const string sql = """
            SELECT COALESCE(SUM(amount), 0)
            FROM refunds WHERE payment_id = @PaymentId AND status = 'completed'
            """;
        return await conn.QuerySingleAsync<decimal>(sql, new { PaymentId = paymentId });
    }
}

// Data transfer objects for Dapper mapping
public record PaymentRecord
{
    public string Id { get; init; } = string.Empty;
    public string MerchantId { get; init; } = string.Empty;
    public string CustomerRef { get; init; } = string.Empty;
    public decimal Amount { get; init; }
    public string Currency { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string IdempotencyKey { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? Metadata { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public AuthRecord? Auth { get; init; }
    public CaptureRecord? Capture { get; init; }
    public IEnumerable<RefundRecord>? Refunds { get; init; }
}

public record AuthRecord
{
    public string Id { get; init; } = string.Empty;
    public string PaymentId { get; init; } = string.Empty;
    public string AuthCode { get; init; } = string.Empty;
    public decimal AuthorisedAmount { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public string Status { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
}

public record CaptureRecord
{
    public string Id { get; init; } = string.Empty;
    public string AuthorisationId { get; init; } = string.Empty;
    public decimal CapturedAmount { get; init; }
    public DateTimeOffset CapturedAt { get; init; }
}

public record RefundRecord
{
    public string Id { get; init; } = string.Empty;
    public string PaymentId { get; init; } = string.Empty;
    public decimal Amount { get; init; }
    public string? Reason { get; init; }
    public string Status { get; init; } = string.Empty;
    public string IdempotencyKey { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
}

public record PaymentEventRecord
{
    public string Id { get; init; } = string.Empty;
    public string PaymentId { get; init; } = string.Empty;
    public string EventType { get; init; } = string.Empty;
    public string Payload { get; init; } = "{}";
    public DateTimeOffset OccurredAt { get; init; }
}

namespace PaymentProcessing.Application.Services;

// ════════════════════════════════════════════════════════════════════════════
// PAYMENT SERVICE
// ════════════════════════════════════════════════════════════════════════════

public class PaymentService : IPaymentService
{
    private readonly PaymentRepository _repo;
    private readonly IAuthorisationService _authService;
    private readonly ILogger<PaymentService> _logger;

    // Valid state transitions — enforced at service layer
    private static readonly Dictionary<string, HashSet<string>> ValidTransitions = new()
    {
        ["pending"]     = ["authorised", "failed"],
        ["authorised"]  = ["captured", "cancelled", "authorisation_expired"],
        ["captured"]    = ["settled", "refunded", "partially_refunded"],
        ["settled"]     = ["refunded", "partially_refunded"],
        ["partially_refunded"] = ["refunded", "partially_refunded"],
    };

    public PaymentService(
        PaymentRepository repo,
        IAuthorisationService authService,
        ILogger<PaymentService> logger)
    {
        _repo        = repo;
        _authService = authService;
        _logger      = logger;
    }

    public async Task<PaymentResponse> CreateAndAuthoriseAsync(
        string idempotencyKey,
        CreatePaymentRequest request,
        CancellationToken ct)
    {
        // Idempotency check
        var existing = await _repo.FindByIdempotencyKeyAsync(idempotencyKey);
        if (existing is not null)
        {
            _logger.LogInformation("Returning idempotent payment result for key {Key}", idempotencyKey);
            return await GetPaymentAsync(existing.Id, ct);
        }

        var paymentId = $"pay_{Guid.NewGuid():N}";
        var amount    = decimal.Parse(request.Amount);

        await using var conn = _repo.CreateConnection();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        try
        {
            // Record as PENDING
            var payment = new PaymentRecord
            {
                Id             = paymentId,
                MerchantId     = "mrc_system", // from auth context in full impl
                CustomerRef    = request.CustomerRef,
                Amount         = amount,
                Currency       = request.Currency,
                Status         = "pending",
                IdempotencyKey = idempotencyKey,
                Description    = request.Description,
                Metadata       = request.Metadata is not null ? JsonSerializer.Serialize(request.Metadata) : "{}",
                CreatedAt      = DateTimeOffset.UtcNow,
                UpdatedAt      = DateTimeOffset.UtcNow
            };
            await _repo.InsertPaymentAsync(payment, conn);
            await _repo.InsertEventAsync(BuildEvent(paymentId, "PaymentCreated",
                new { payment.Amount, payment.Currency }), conn);

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }

        // Authorisation call OUTSIDE the transaction — it involves an external I/O call
        // If authorisation fails, we update the payment status to 'failed'
        try
        {
            var authInfo = await _authService.AuthoriseAsync(paymentId, amount, request.Currency, ct);

            await using var authConn = _repo.CreateConnection();
            await authConn.OpenAsync(ct);
            await using var authTx = await authConn.BeginTransactionAsync(ct);

            var authRecord = new AuthRecord
            {
                Id                = $"auth_{Guid.NewGuid():N}",
                PaymentId         = paymentId,
                AuthCode          = authInfo.AuthCode,
                AuthorisedAmount  = decimal.Parse(authInfo.AuthorisedAmount),
                ExpiresAt         = authInfo.ExpiresAt,
                Status            = "active",
                CreatedAt         = DateTimeOffset.UtcNow
            };
            await _repo.InsertAuthorisationAsync(authRecord, authConn);
            await _repo.UpdateStatusAsync(paymentId, "authorised", authConn);
            await _repo.InsertEventAsync(BuildEvent(paymentId, "PaymentAuthorised",
                new { authRecord.AuthCode, authRecord.AuthorisedAmount, authRecord.ExpiresAt }), authConn);

            await authTx.CommitAsync(ct);
            _logger.LogInformation("Payment {PaymentId} authorised with code {AuthCode}", paymentId, authInfo.AuthCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Authorisation failed for payment {PaymentId}", paymentId);
            await using var failConn = _repo.CreateConnection();
            await failConn.OpenAsync(ct);
            await _repo.UpdateStatusAsync(paymentId, "failed", failConn);
        }

        return await GetPaymentAsync(paymentId, ct);
    }

    public async Task<PaymentResponse> GetPaymentAsync(string paymentId, CancellationToken ct)
    {
        var p = await _repo.FindByIdAsync(paymentId)
            ?? throw new PaymentNotFoundException(paymentId);
        return MapPaymentResponse(p);
    }

    public async Task<IEnumerable<PaymentEventResponse>> GetPaymentEventsAsync(string paymentId, CancellationToken ct)
    {
        var events = await _repo.GetEventsAsync(paymentId);
        return events.Select(e => new PaymentEventResponse
        {
            Id         = e.Id,
            PaymentId  = e.PaymentId,
            EventType  = e.EventType,
            Payload    = JsonSerializer.Deserialize<Dictionary<string, object>>(e.Payload),
            OccurredAt = e.OccurredAt
        });
    }

    public async Task<PaymentResponse> RefundAsync(
        string paymentId,
        string idempotencyKey,
        RefundPaymentRequest request,
        CancellationToken ct)
    {
        var payment = await _repo.FindByIdAsync(paymentId)
            ?? throw new PaymentNotFoundException(paymentId);

        if (payment.Status is not ("captured" or "settled" or "partially_refunded"))
            throw new InvalidStateTransitionException(paymentId, payment.Status, "refund");

        if (payment.Capture is null)
            throw new InvalidOperationException($"Payment {paymentId} has no capture record.");

        var refundAmount = decimal.Parse(request.Amount);
        var totalRefunded = await _repo.GetTotalRefundedAsync(paymentId);

        if (totalRefunded + refundAmount > payment.Capture.CapturedAmount)
            throw new RefundExceedsCaptureException(paymentId, payment.Capture.CapturedAmount, totalRefunded + refundAmount);

        await using var conn = _repo.CreateConnection();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        try
        {
            var isFullRefund = totalRefunded + refundAmount >= payment.Capture.CapturedAmount;
            var newStatus    = isFullRefund ? "refunded" : "partially_refunded";

            var refundRecord = new RefundRecord
            {
                Id             = $"ref_{Guid.NewGuid():N}",
                PaymentId      = paymentId,
                Amount         = refundAmount,
                Reason         = request.Reason,
                Status         = "completed",
                IdempotencyKey = idempotencyKey,
                CreatedAt      = DateTimeOffset.UtcNow
            };
            await _repo.InsertRefundAsync(refundRecord, conn);
            await _repo.UpdateStatusAsync(paymentId, newStatus, conn);
            await _repo.InsertEventAsync(BuildEvent(paymentId, "PaymentRefunded",
                new { Amount = refundAmount, request.Reason }), conn);

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }

        return await GetPaymentAsync(paymentId, ct);
    }

    public async Task<PaymentResponse> CancelAsync(string paymentId, CancellationToken ct)
    {
        var payment = await _repo.FindByIdAsync(paymentId)
            ?? throw new PaymentNotFoundException(paymentId);

        if (payment.Status != "authorised")
            throw new InvalidStateTransitionException(paymentId, payment.Status, "cancelled");

        await using var conn = _repo.CreateConnection();
        await conn.OpenAsync(ct);
        await _repo.UpdateStatusAsync(paymentId, "cancelled", conn);
        await _repo.InsertEventAsync(BuildEvent(paymentId, "PaymentCancelled", new { }), conn);

        return await GetPaymentAsync(paymentId, ct);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static PaymentEventRecord BuildEvent(string paymentId, string eventType, object payload) => new()
    {
        Id          = $"evt_{Guid.NewGuid():N}",
        PaymentId   = paymentId,
        EventType   = eventType,
        Payload     = JsonSerializer.Serialize(payload),
        OccurredAt  = DateTimeOffset.UtcNow
    };

    private static PaymentResponse MapPaymentResponse(PaymentRecord p) => new()
    {
        Id          = p.Id,
        Amount      = p.Amount.ToString("F2"),
        Currency    = p.Currency,
        Status      = p.Status.ToUpper(),
        CustomerRef = p.CustomerRef,
        Description = p.Description,
        Authorisation = p.Auth is null ? null : new AuthorisationInfo
        {
            AuthCode         = p.Auth.AuthCode,
            AuthorisedAmount = p.Auth.AuthorisedAmount.ToString("F2"),
            ExpiresAt        = p.Auth.ExpiresAt
        },
        Capture = p.Capture is null ? null : new CaptureInfo
        {
            CapturedAmount = p.Capture.CapturedAmount.ToString("F2"),
            CapturedAt     = p.Capture.CapturedAt
        },
        Refunds = (p.Refunds ?? []).Select(r => new RefundInfo
        {
            RefundId   = r.Id,
            Amount     = r.Amount.ToString("F2"),
            Reason     = r.Reason,
            RefundedAt = r.CreatedAt
        }),
        CreatedAt = p.CreatedAt,
        UpdatedAt = p.UpdatedAt
    };
}

// ════════════════════════════════════════════════════════════════════════════
// AUTHORISATION SERVICE (simulated external processor)
// ════════════════════════════════════════════════════════════════════════════

public class AuthorisationService : IAuthorisationService
{
    private readonly ILogger<AuthorisationService> _logger;

    public AuthorisationService(ILogger<AuthorisationService> logger)
    {
        _logger = logger;
    }

    public async Task<AuthorisationInfo> AuthoriseAsync(
        string paymentId, decimal amount, string currency, CancellationToken ct)
    {
        // Simulated processor call — replace with real gateway integration
        await Task.Delay(50, ct); // simulate network round-trip

        // Simulate occasional declines for testing
        if (amount > 50_000m)
            throw new ProcessorDeclinedException(paymentId, "INSUFFICIENT_FUNDS");

        var authCode = $"AUTH_{Guid.NewGuid().ToString("N")[..8].ToUpper()}";
        _logger.LogInformation("Authorised payment {PaymentId} with code {AuthCode}", paymentId, authCode);

        return new AuthorisationInfo
        {
            AuthCode         = authCode,
            AuthorisedAmount = amount.ToString("F2"),
            ExpiresAt        = DateTimeOffset.UtcNow.AddHours(24)
        };
    }
}

// ════════════════════════════════════════════════════════════════════════════
// CAPTURE SERVICE
// ════════════════════════════════════════════════════════════════════════════

public class CaptureService : ICaptureService
{
    private readonly PaymentRepository _repo;
    private readonly ILogger<CaptureService> _logger;

    public CaptureService(PaymentRepository repo, ILogger<CaptureService> logger)
    {
        _repo   = repo;
        _logger = logger;
    }

    public async Task<PaymentResponse> CaptureAsync(
        string paymentId,
        string idempotencyKey,
        CapturePaymentRequest request,
        CancellationToken ct)
    {
        var payment = await _repo.FindByIdAsync(paymentId)
            ?? throw new PaymentNotFoundException(paymentId);

        if (payment.Status != "authorised")
            throw new InvalidStateTransitionException(paymentId, payment.Status, "captured");

        if (payment.Auth is null)
            throw new InvalidOperationException($"No authorisation record for payment {paymentId}.");

        // Enforce authorisation expiry
        if (payment.Auth.ExpiresAt < DateTimeOffset.UtcNow)
            throw new AuthorisationExpiredException(paymentId, payment.Auth.ExpiresAt);

        var captureAmount = decimal.Parse(request.Amount);
        if (captureAmount > payment.Auth.AuthorisedAmount)
            throw new CaptureExceedsAuthorisationException(paymentId, payment.Auth.AuthorisedAmount, captureAmount);

        await using var conn = _repo.CreateConnection();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        try
        {
            var capture = new CaptureRecord
            {
                Id               = $"cap_{Guid.NewGuid():N}",
                AuthorisationId  = payment.Auth.Id,
                CapturedAmount   = captureAmount,
                CapturedAt       = DateTimeOffset.UtcNow
            };
            await _repo.InsertCaptureAsync(capture, conn);
            await _repo.UpdateStatusAsync(paymentId, "captured", conn);
            await _repo.InsertEventAsync(new PaymentEventRecord
            {
                Id          = $"evt_{Guid.NewGuid():N}",
                PaymentId   = paymentId,
                EventType   = "PaymentCaptured",
                Payload     = JsonSerializer.Serialize(new { CapturedAmount = captureAmount }),
                OccurredAt  = DateTimeOffset.UtcNow
            }, conn);

            await tx.CommitAsync(ct);
            _logger.LogInformation("Payment {PaymentId} captured for {Amount}", paymentId, captureAmount);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }

        return await new PaymentService(_repo, new AuthorisationService(_logger as ILogger<AuthorisationService> ?? throw new InvalidOperationException()), _logger as ILogger<PaymentService> ?? throw new InvalidOperationException()).GetPaymentAsync(paymentId, ct);
    }
}

// ── Payment exceptions ────────────────────────────────────────────────────────

public class PaymentNotFoundException(string id)
    : Exception($"Payment '{id}' not found.");

public class InvalidStateTransitionException(string paymentId, string fromStatus, string toStatus)
    : Exception($"Cannot transition payment '{paymentId}' from '{fromStatus}' to '{toStatus}'.");

public class AuthorisationExpiredException(string paymentId, DateTimeOffset expiredAt)
    : Exception($"Authorisation for payment '{paymentId}' expired at {expiredAt:O}.");

public class CaptureExceedsAuthorisationException(string paymentId, decimal authorised, decimal requested)
    : Exception($"Capture amount {requested} exceeds authorised amount {authorised} for payment '{paymentId}'.");

public class RefundExceedsCaptureException(string paymentId, decimal captured, decimal requestedTotal)
    : Exception($"Total refund {requestedTotal} would exceed captured amount {captured} for payment '{paymentId}'.");

public class ProcessorDeclinedException(string paymentId, string code)
    : Exception($"Processor declined payment '{paymentId}': {code}.");
