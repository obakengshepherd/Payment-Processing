using StackExchange.Redis;

namespace PaymentProcessing.Infrastructure.Cache;

/// <summary>
/// Redis idempotency cache for the Payment Processing system.
///
/// Pattern: SETNX (SET if Not eXists) with TTL
/// Prevents duplicate payment processing within the 24-hour idempotency window.
///
/// Key: idempotency:payment:{merchantId}:{key}
/// Value: serialised PaymentResponse JSON
/// TTL: 24 hours
///
/// Behaviour:
///   - First request: SET NX succeeds → record not found → process payment → cache result
///   - Duplicate request within 24h: GET hits → return cached response → skip processing
///   - After 24h: key expires → treat as new request
/// </summary>
public class PaymentCacheService
{
    private readonly IDatabase _db;
    private readonly ILogger<PaymentCacheService> _logger;

    private static readonly TimeSpan IdempotencyTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan AuthStatusTtl  = TimeSpan.FromMinutes(5);

    public PaymentCacheService(IConnectionMultiplexer redis, ILogger<PaymentCacheService> logger)
    {
        _db     = redis.GetDatabase();
        _logger = logger;
    }

    // ── Idempotency — SETNX pattern ───────────────────────────────────────────

    /// <summary>
    /// Returns the cached payment response if this idempotency key was already processed.
    /// Returns null if this is a new request.
    /// </summary>
    public async Task<string?> GetIdempotencyResultAsync(string merchantId, string idempotencyKey)
    {
        try
        {
            var value = await _db.StringGetAsync(IdempotencyKey(merchantId, idempotencyKey));
            return value.HasValue ? value.ToString() : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis idempotency read failed — falling back to DB check");
            return null;
        }
    }

    /// <summary>
    /// Caches the payment result for this idempotency key.
    /// Uses NX flag — if the key already exists (race condition), this is a no-op.
    /// The first response wins; subsequent attempts return what's already cached.
    /// </summary>
    public async Task SetIdempotencyResultAsync(string merchantId, string idempotencyKey, string serialisedResponse)
    {
        try
        {
            await _db.StringSetAsync(
                IdempotencyKey(merchantId, idempotencyKey),
                serialisedResponse,
                IdempotencyTtl,
                When.NotExists);  // NX — atomic test-and-set, first writer wins
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis idempotency write failed — non-fatal (DB unique index is fallback)");
        }
    }

    // ── Payment status cache — for high-read status endpoints ─────────────────

    public async Task<string?> GetPaymentStatusAsync(string paymentId)
    {
        try
        {
            var value = await _db.StringGetAsync(StatusKey(paymentId));
            return value.HasValue ? value.ToString() : null;
        }
        catch { return null; }
    }

    public async Task SetPaymentStatusAsync(string paymentId, string status)
    {
        try
        {
            // Short TTL — payment status changes frequently during lifecycle
            await _db.StringSetAsync(StatusKey(paymentId), status, AuthStatusTtl);
        }
        catch { /* non-fatal */ }
    }

    public async Task InvalidatePaymentStatusAsync(string paymentId)
    {
        try { await _db.KeyDeleteAsync(StatusKey(paymentId)); }
        catch { /* non-fatal */ }
    }

    // ── Key builders ──────────────────────────────────────────────────────────

    private static string IdempotencyKey(string merchantId, string key)
        => $"idempotency:payment:{merchantId}:{key}";

    private static string StatusKey(string paymentId)
        => $"payment:status:{paymentId}";
}

// ════════════════════════════════════════════════════════════════════════════
// KAFKA EVENT PUBLISHER — Payment Processing
// ════════════════════════════════════════════════════════════════════════════

using System.Text.Json;
using Confluent.Kafka;

namespace PaymentProcessing.Infrastructure.Messaging;

/// <summary>
/// Kafka producer for payment lifecycle events.
///
/// Topics:
///   payments.events     — all lifecycle events, partitioned by payment_id
///   payments.settlement — captured payments awaiting settlement, partitioned by merchant_id
///
/// All events published AFTER database commit — never speculatively.
/// </summary>
public class PaymentEventPublisher : IDisposable
{
    private readonly IProducer<string, string> _producer;
    private readonly ILogger<PaymentEventPublisher> _logger;

    private const string EventsTopic      = "payments.events";
    private const string SettlementTopic  = "payments.settlement";

    public PaymentEventPublisher(IConfiguration configuration, ILogger<PaymentEventPublisher> logger)
    {
        _logger = logger;
        _producer = new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers      = configuration.GetConnectionString("Kafka") ?? "localhost:9092",
            Acks                  = Acks.All,
            EnableIdempotence     = true,
            MessageSendMaxRetries = 3,
            RetryBackoffMs        = 100
        }).Build();
    }

    public async Task PublishPaymentEventAsync(string paymentId, string eventType, object payload)
    {
        var message = new PaymentKafkaEvent
        {
            EventId    = Guid.NewGuid().ToString(),
            EventType  = eventType,
            PaymentId  = paymentId,
            OccurredAt = DateTimeOffset.UtcNow,
            Payload    = payload
        };

        try
        {
            var result = await _producer.ProduceAsync(EventsTopic, new Message<string, string>
            {
                Key   = paymentId,           // partition by payment_id for ordering
                Value = JsonSerializer.Serialize(message)
            });

            _logger.LogInformation("Published {EventType} for payment {PaymentId} → {Topic}/{Partition}@{Offset}",
                eventType, paymentId, EventsTopic, result.Partition, result.Offset);
        }
        catch (ProduceException<string, string> ex)
        {
            _logger.LogError(ex, "Failed to publish {EventType} for payment {PaymentId}", eventType, paymentId);
            throw;
        }
    }

    public async Task PublishForSettlementAsync(string paymentId, string merchantId, decimal capturedAmount, string currency)
    {
        try
        {
            await _producer.ProduceAsync(SettlementTopic, new Message<string, string>
            {
                Key   = merchantId,          // partition by merchant for settlement batch processing
                Value = JsonSerializer.Serialize(new
                {
                    PaymentId      = paymentId,
                    MerchantId     = merchantId,
                    CapturedAmount = capturedAmount,
                    Currency       = currency,
                    CapturedAt     = DateTimeOffset.UtcNow
                })
            });

            _logger.LogInformation("Queued payment {PaymentId} for settlement", paymentId);
        }
        catch (ProduceException<string, string> ex)
        {
            _logger.LogError(ex, "Failed to queue payment {PaymentId} for settlement", paymentId);
            throw;
        }
    }

    public void Dispose() => _producer?.Dispose();

    private record PaymentKafkaEvent
    {
        public string EventId { get; init; } = string.Empty;
        public string EventType { get; init; } = string.Empty;
        public string PaymentId { get; init; } = string.Empty;
        public DateTimeOffset OccurredAt { get; init; }
        public object Payload { get; init; } = new();
    }
}

// ════════════════════════════════════════════════════════════════════════════
// KAFKA CONSUMER — Settlement Consumer
// Processes payments.settlement events and marks payments as SETTLED
// ════════════════════════════════════════════════════════════════════════════

using Dapper;
using Npgsql;

namespace PaymentProcessing.Infrastructure.Messaging;

/// <summary>
/// Consumes from payments.settlement topic and performs settlement.
///
/// Consumer group: payment-settlement-consumer
/// Topic: payments.settlement (partitioned by merchant_id)
///
/// Dead letter: settlement failures after MaxRetries are written to
/// a dead_letter table in PostgreSQL for manual resolution.
/// </summary>
public class SettlementKafkaConsumer : IDisposable
{
    private readonly IConsumer<string, string> _consumer;
    private readonly string _connectionString;
    private readonly PaymentEventPublisher _publisher;
    private readonly ILogger<SettlementKafkaConsumer> _logger;
    private const string Topic    = "payments.settlement";
    private const int MaxRetries  = 5;

    public SettlementKafkaConsumer(
        IConfiguration configuration,
        PaymentEventPublisher publisher,
        ILogger<SettlementKafkaConsumer> logger)
    {
        _publisher        = publisher;
        _logger           = logger;
        _connectionString = configuration.GetConnectionString("PostgreSQL")!;

        _consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers       = configuration.GetConnectionString("Kafka") ?? "localhost:9092",
            GroupId                = "payment-settlement-consumer",
            AutoOffsetReset        = AutoOffsetReset.Earliest,
            EnableAutoCommit       = false,
            EnableAutoOffsetStore  = false,
            IsolationLevel         = IsolationLevel.ReadCommitted,
            MaxPollIntervalMs      = 600000   // settlement can take up to 10 min
        }).Build();
    }

    public async Task ConsumeAsync(CancellationToken ct)
    {
        _consumer.Subscribe(Topic);
        _logger.LogInformation("SettlementConsumer started — group: payment-settlement-consumer");

        while (!ct.IsCancellationRequested)
        {
            ConsumeResult<string, string>? result = null;
            try
            {
                result = _consumer.Consume(TimeSpan.FromSeconds(2));
                if (result is null || result.IsPartitionEOF) continue;

                var success = await ProcessWithRetryAsync(result, ct);
                if (!success)
                    await WriteToDeadLetterTableAsync(result, "Max retries exhausted");

                _consumer.StoreOffset(result);
                _consumer.Commit(result);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Critical error in settlement consumer");
                if (result is not null)
                {
                    await WriteToDeadLetterTableAsync(result, ex.Message);
                    _consumer.StoreOffset(result);
                    _consumer.Commit(result);
                }
            }
        }
        _consumer.Close();
    }

    private async Task<bool> ProcessWithRetryAsync(ConsumeResult<string, string> result, CancellationToken ct)
    {
        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                await SettlePaymentAsync(result.Message.Value, ct);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Settlement attempt {Attempt}/{Max} failed", attempt + 1, MaxRetries);
                if (attempt < MaxRetries - 1)
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
            }
        }
        return false;
    }

    private async Task SettlePaymentAsync(string messageValue, CancellationToken ct)
    {
        var msg = JsonSerializer.Deserialize<SettlementMessage>(messageValue)!;

        // Simulated settlement processor call
        await Task.Delay(100, ct);

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync("""
            UPDATE payments
            SET status = 'settled'::payment_status, updated_at = NOW()
            WHERE id = @PaymentId AND status = 'captured'::payment_status
            """, new { PaymentId = msg.PaymentId });

        await conn.ExecuteAsync("""
            INSERT INTO payment_events (id, payment_id, event_type, payload, occurred_at)
            VALUES (@Id, @PaymentId, 'PaymentSettled', @Payload::jsonb, NOW())
            """, new
        {
            Id        = $"evt_{Guid.NewGuid():N}",
            PaymentId = msg.PaymentId,
            Payload   = JsonSerializer.Serialize(new { msg.MerchantId, msg.CapturedAmount })
        });

        // Publish settled event for downstream consumers
        await _publisher.PublishPaymentEventAsync(msg.PaymentId, "PaymentSettled",
            new { msg.MerchantId, msg.CapturedAmount, msg.Currency });

        _logger.LogInformation("Payment {PaymentId} settled for merchant {MerchantId}", msg.PaymentId, msg.MerchantId);
    }

    private async Task WriteToDeadLetterTableAsync(ConsumeResult<string, string> result, string reason)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();
            await conn.ExecuteAsync("""
                INSERT INTO payment_events (id, payment_id, event_type, payload, occurred_at)
                VALUES (@Id, 'DLQ', 'SettlementFailed', @Payload::jsonb, NOW())
                """, new
            {
                Id      = $"evt_{Guid.NewGuid():N}",
                Payload = JsonSerializer.Serialize(new
                {
                    OriginalValue = result.Message.Value,
                    Reason        = reason,
                    Partition     = result.Partition.Value,
                    Offset        = result.Offset.Value
                })
            });
        }
        catch (Exception ex) { _logger.LogError(ex, "Failed to write to dead letter table"); }
    }

    private record SettlementMessage(string PaymentId, string MerchantId, decimal CapturedAmount, string Currency);
    public void Dispose() => _consumer?.Dispose();
}

/// <summary>
/// Hosted service wrapper for the settlement consumer.
/// Register: builder.Services.AddHostedService<SettlementConsumerWorker>();
/// </summary>
public class SettlementConsumerWorker : BackgroundService
{
    private readonly SettlementKafkaConsumer _consumer;

    public SettlementConsumerWorker(SettlementKafkaConsumer consumer) => _consumer = consumer;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        => await _consumer.ConsumeAsync(stoppingToken);
}
