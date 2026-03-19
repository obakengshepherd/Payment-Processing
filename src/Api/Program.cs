using PaymentProcessing.Infrastructure.Cache;
using PaymentProcessing.Infrastructure.Messaging;
using StackExchange.Redis;
using Shared.Infrastructure.RateLimit;
using Shared.Api.Controllers;
using Microsoft.Extensions.Diagnostics.HealthChecks;

builder.Services.AddSingleton<IEnumerable<RateLimitRule>>(
    _ => RateLimitPolicies.PaymentPolicies());

builder.Services.AddSingleton<TrueSlidingWindowChecker>();

builder.Services.AddHealthChecks()
    .AddCheck<RedisHealthCheck>("redis",       failureStatus: HealthStatus.Degraded,  tags: ["cache"])
    .AddCheck<KafkaHealthCheck>("kafka",       failureStatus: HealthStatus.Degraded,  tags: ["messaging"])
    .AddCheck<PostgreSqlHealthCheck>("postgresql", failureStatus: HealthStatus.Unhealthy, tags: ["database"]);

builder.Services.AddTransient<RedisHealthCheck>();
builder.Services.AddTransient(_ => new PostgreSqlHealthCheck(builder.Configuration.GetConnectionString("PostgreSQL")!));
builder.Services.AddTransient(_ => new KafkaHealthCheck(builder.Configuration.GetConnectionString("Kafka") ?? "localhost:9092"));

// ── Middleware pipeline ──
// app.UseAuthentication();
// app.UseAuthorization();
// app.UseMiddleware<RedisRateLimitMiddleware>();
// app.UseMiddleware<IdempotencyMiddleware>();
// app.MapControllers();
// app.MapHealthEndpoints();

// Redis
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(
        builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379"));
builder.Services.AddSingleton<PaymentCacheService>();

// Kafka producer
builder.Services.AddSingleton<PaymentEventPublisher>();

// Kafka consumer (settlement)
builder.Services.AddSingleton<SettlementKafkaConsumer>();
builder.Services.AddHostedService<SettlementConsumerWorker>();

// Repositories and services
builder.Services.AddScoped<PaymentRepository>();
builder.Services.AddScoped<IPaymentService, PaymentService>();
builder.Services.AddScoped<IAuthorisationService, AuthorisationService>();
builder.Services.AddScoped<ICaptureService, CaptureService>();

// Health checks
builder.Services.AddHealthChecks()
    .AddNpgsql(builder.Configuration.GetConnectionString("PostgreSQL")!)
    .AddRedis(builder.Configuration.GetConnectionString("Redis")!);