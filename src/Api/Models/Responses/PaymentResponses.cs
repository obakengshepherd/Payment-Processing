namespace PaymentProcessing.Api.Models.Responses;

public record PaymentResponse
{
    public string Id { get; init; } = string.Empty;
    public string Amount { get; init; } = string.Empty;
    public string Currency { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string CustomerRef { get; init; } = string.Empty;
    public string? Description { get; init; }
    public AuthorisationInfo? Authorisation { get; init; }
    public CaptureInfo? Capture { get; init; }
    public IEnumerable<RefundInfo> Refunds { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public record AuthorisationInfo
{
    public string AuthCode { get; init; } = string.Empty;
    public string AuthorisedAmount { get; init; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; init; }
}

public record CaptureInfo
{
    public string CapturedAmount { get; init; } = string.Empty;
    public DateTimeOffset CapturedAt { get; init; }
}

public record RefundInfo
{
    public string RefundId { get; init; } = string.Empty;
    public string Amount { get; init; } = string.Empty;
    public string? Reason { get; init; }
    public DateTimeOffset RefundedAt { get; init; }
}

public record PaymentEventResponse
{
    public string Id { get; init; } = string.Empty;
    public string PaymentId { get; init; } = string.Empty;
    public string EventType { get; init; } = string.Empty;
    public Dictionary<string, object>? Payload { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
}

public record ApiResponse<T> { public T Data { get; init; } = default!; public ApiMeta Meta { get; init; } = new(); }
public record ApiMeta { public string RequestId { get; init; } = Guid.NewGuid().ToString(); public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow; }
public static class ApiResponse { public static ApiResponse<T> Success<T>(T data) => new() { Data = data }; }