namespace PaymentProcessing.Application.Interfaces;

using PaymentProcessing.Api.Models.Requests;
using PaymentProcessing.Api.Models.Responses;

/// <summary>
/// Owns the full payment lifecycle: create+authorise, refund, cancel, read.
/// Every write records an immutable event in payment_events.
/// </summary>
public interface IPaymentService
{
    Task<PaymentResponse> CreateAndAuthoriseAsync(string idempotencyKey, CreatePaymentRequest request, CancellationToken ct);
    Task<PaymentResponse> RefundAsync(string paymentId, string idempotencyKey, RefundPaymentRequest request, CancellationToken ct);
    Task<PaymentResponse> CancelAsync(string paymentId, CancellationToken ct);
    Task<PaymentResponse> GetPaymentAsync(string paymentId, CancellationToken ct);
    Task<IEnumerable<PaymentEventResponse>> GetPaymentEventsAsync(string paymentId, CancellationToken ct);
}

/// <summary>Wraps the simulated external processor authorisation call.</summary>
public interface IAuthorisationService
{
    Task<AuthorisationInfo> AuthoriseAsync(string paymentId, decimal amount, string currency, CancellationToken ct);
}

/// <summary>
/// Handles payment capture with state validation.
/// Publishes PaymentCaptured event to Kafka for async settlement.
/// </summary>
public interface ICaptureService
{
    Task<PaymentResponse> CaptureAsync(string paymentId, string idempotencyKey, CapturePaymentRequest request, CancellationToken ct);
}