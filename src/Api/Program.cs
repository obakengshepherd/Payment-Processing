using PaymentProcessing.Infrastructure.Cache;
using PaymentProcessing.Infrastructure.Messaging;
using StackExchange.Redis;

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