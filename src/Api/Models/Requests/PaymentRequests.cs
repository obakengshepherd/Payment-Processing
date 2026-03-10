namespace PaymentProcessing.Api.Models.Requests;

using System.ComponentModel.DataAnnotations;

public record CreatePaymentRequest
{
    [Required] public string Amount { get; init; } = string.Empty; // decimal as string
    [Required][StringLength(3, MinimumLength = 3)] public string Currency { get; init; } = string.Empty;
    [Required][StringLength(128)] public string CustomerRef { get; init; } = string.Empty;
    [StringLength(256)] public string? Description { get; init; }
    public Dictionary<string, string>? Metadata { get; init; }
}

public record CapturePaymentRequest
{
    [Required] public string Amount { get; init; } = string.Empty;
}

public record RefundPaymentRequest
{
    [Required] public string Amount { get; init; } = string.Empty;
    [StringLength(256)] public string? Reason { get; init; }
}
