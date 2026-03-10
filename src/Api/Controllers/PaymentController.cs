using PaymentProcessing.Application.Interfaces;
using PaymentProcessing.Api.Models.Requests;
using PaymentProcessing.Api.Models.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace PaymentProcessing.Api.Controllers;

[ApiController]
[Route("api/v1/payments")]
[Authorize]
public class PaymentController : ControllerBase
{
    private readonly IPaymentService _paymentService;
    private readonly IAuthorisationService _authorisationService;
    private readonly ICaptureService _captureService;

    public PaymentController(
        IPaymentService paymentService,
        IAuthorisationService authorisationService,
        ICaptureService captureService)
    {
        _paymentService = paymentService;
        _authorisationService = authorisationService;
        _captureService = captureService;
    }

    // POST /api/v1/payments
    [HttpPost]
    public async Task<IActionResult> CreatePayment(
        [FromBody] CreatePaymentRequest request,
        [FromHeader(Name = "X-Idempotency-Key")] string idempotencyKey,
        CancellationToken ct)
    {
        var result = await _paymentService.CreateAndAuthoriseAsync(idempotencyKey, request, ct);
        return StatusCode(StatusCodes.Status201Created, ApiResponse.Success(result));
    }

    // POST /api/v1/payments/{id}/capture
    [HttpPost("{id}/capture")]
    public async Task<IActionResult> Capture(
        [FromRoute] string id,
        [FromBody] CapturePaymentRequest request,
        [FromHeader(Name = "X-Idempotency-Key")] string idempotencyKey,
        CancellationToken ct)
    {
        var result = await _captureService.CaptureAsync(id, idempotencyKey, request, ct);
        return Ok(ApiResponse.Success(result));
    }

    // POST /api/v1/payments/{id}/refund
    [HttpPost("{id}/refund")]
    public async Task<IActionResult> Refund(
        [FromRoute] string id,
        [FromBody] RefundPaymentRequest request,
        [FromHeader(Name = "X-Idempotency-Key")] string idempotencyKey,
        CancellationToken ct)
    {
        var result = await _paymentService.RefundAsync(id, idempotencyKey, request, ct);
        return Ok(ApiResponse.Success(result));
    }

    // GET /api/v1/payments/{id}
    [HttpGet("{id}")]
    public async Task<IActionResult> GetPayment([FromRoute] string id, CancellationToken ct)
    {
        var result = await _paymentService.GetPaymentAsync(id, ct);
        return Ok(ApiResponse.Success(result));
    }

    // GET /api/v1/payments/{id}/events
    [HttpGet("{id}/events")]
    public async Task<IActionResult> GetPaymentEvents([FromRoute] string id, CancellationToken ct)
    {
        var result = await _paymentService.GetPaymentEventsAsync(id, ct);
        return Ok(ApiResponse.Success(result));
    }

    // POST /api/v1/payments/{id}/cancel
    [HttpPost("{id}/cancel")]
    public async Task<IActionResult> CancelPayment([FromRoute] string id, CancellationToken ct)
    {
        var result = await _paymentService.CancelAsync(id, ct);
        return Ok(ApiResponse.Success(result));
    }
}