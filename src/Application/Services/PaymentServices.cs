namespace PaymentProcessing.Application.Services;

using PaymentProcessing.Application.Interfaces;
using PaymentProcessing.Api.Models.Requests;
using PaymentProcessing.Api.Models.Responses;

public class PaymentService : IPaymentService
{
    public Task<PaymentResponse> CreateAndAuthoriseAsync(string idempotencyKey, CreatePaymentRequest request, CancellationToken ct) => throw new NotImplementedException("Implemented Day 12");
    public Task<PaymentResponse> RefundAsync(string paymentId, string idempotencyKey, RefundPaymentRequest request, CancellationToken ct) => throw new NotImplementedException("Implemented Day 12");
    public Task<PaymentResponse> CancelAsync(string paymentId, CancellationToken ct) => throw new NotImplementedException("Implemented Day 12");
    public Task<PaymentResponse> GetPaymentAsync(string paymentId, CancellationToken ct) => throw new NotImplementedException("Implemented Day 12");
    public Task<IEnumerable<PaymentEventResponse>> GetPaymentEventsAsync(string paymentId, CancellationToken ct) => throw new NotImplementedException("Implemented Day 12");
}

public class AuthorisationService : IAuthorisationService
{
    public Task<AuthorisationInfo> AuthoriseAsync(string paymentId, decimal amount, string currency, CancellationToken ct) => throw new NotImplementedException("Implemented Day 12");
}

public class CaptureService : ICaptureService
{
    public Task<PaymentResponse> CaptureAsync(string paymentId, string idempotencyKey, CapturePaymentRequest request, CancellationToken ct) => throw new NotImplementedException("Implemented Day 12");
}
