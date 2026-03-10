namespace PaymentProcessing.Domain.Entities;

public class Payment
{
    public string Id { get; private set; } = string.Empty;
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = string.Empty;
    public PaymentStatus Status { get; private set; }
    public string CustomerRef { get; private set; } = string.Empty;
    public string IdempotencyKey { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    // State machine — implemented Day 12
    public void Authorise(string authCode, decimal authorisedAmount, DateTimeOffset expiresAt) => throw new NotImplementedException();
    public void Capture(decimal amount) => throw new NotImplementedException();
    public void Refund(decimal amount) => throw new NotImplementedException();
    public void Cancel() => throw new NotImplementedException();
    public void Settle() => throw new NotImplementedException();
    private void TransitionTo(PaymentStatus newStatus) => throw new NotImplementedException();
}

public class PaymentEvent
{
    public string Id { get; private set; } = string.Empty;
    public string PaymentId { get; private set; } = string.Empty;
    public string EventType { get; private set; } = string.Empty;
    public string PayloadJson { get; private set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; private set; }
}

public enum PaymentStatus
{
    Pending, Authorised, AuthorisationExpired, Captured, Settled,
    Refunded, PartiallyRefunded, Cancelled, Failed
}

namespace PaymentProcessing.Api;
using System.Security.Claims;
public static class ClaimsPrincipalExtensions
{
    public static string GetUserId(this ClaimsPrincipal p)
    {
        var id = p.FindFirstValue(ClaimTypes.NameIdentifier) ?? p.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(id)) throw new UnauthorizedAccessException("User ID claim missing.");
        return id;
    }
}