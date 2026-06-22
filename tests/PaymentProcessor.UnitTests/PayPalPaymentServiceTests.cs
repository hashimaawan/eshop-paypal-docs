#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PaypalServerSdk.Standard;

namespace eShop.PaymentProcessor.UnitTests;

/// <summary>
/// Unit tests for <see cref="PayPalPaymentService"/> over the PayPal Server SDK.
///
/// The external HTTP boundary is stubbed by handing the real SDK client a fake
/// <see cref="HttpMessageHandler"/> (via <see cref="PayPalSdkClientFactory"/>), so the tests assert the
/// real request shape the SDK produces (idempotency header, Prefer header, path/method) and the way the
/// service maps each PayPal response/outcome. The SDK's own retry layer is disabled in the factory, so
/// each case sees exactly the calls it makes.
/// </summary>
[TestClass]
public sealed class PayPalPaymentServiceTests
{
    private const int OrderId = 42;
    private const string AuthorizationId = "AUTH-1";

    // ---------- Happy path ----------

    [TestMethod]
    public async Task ProcessPaymentAsync_WhenCaptureCompleted_ReturnsTrueAndRecordsCaptureId()
    {
        var handler = new MockPayPalHandler();
        var (sut, ordering) = CreateSut(handler);

        var result = await sut.ProcessPaymentAsync(OrderId);

        Assert.IsTrue(result);

        // Idempotency key (C4) is sent on the capture, derived from order + authorization.
        var capture = handler.LastRequestTo("/capture");
        Assert.IsNotNull(capture);
        Assert.AreEqual($"capture-{OrderId}-{AuthorizationId}", capture!.PayPalRequestId);

        // §5: the capture id is recorded back onto the order.
        await ordering.Received(1).UpdatePayPalReferencesAsync(OrderId, null, "CAP-1", Arg.Any<CancellationToken>());
    }

    // ---------- Business decline / non-completed ----------

    [TestMethod]
    public async Task ProcessPaymentAsync_WhenCaptureStatusNotCompleted_ReturnsFalse()
    {
        var handler = new MockPayPalHandler { CaptureStatus = "DECLINED" };
        var (sut, _) = CreateSut(handler);

        Assert.IsFalse(await sut.ProcessPaymentAsync(OrderId));
    }

    [TestMethod]
    public async Task ProcessPaymentAsync_WhenCaptureReturns422Decline_ReturnsFalse()
    {
        // 422 carries the typed ErrorException; a decline is a normal failed outcome, not a thrown 500.
        var handler = new MockPayPalHandler
        {
            CaptureHttpStatus = (HttpStatusCode)422,
            CaptureBody = """{"name":"UNPROCESSABLE_ENTITY","message":"Declined","debug_id":"d1","details":[{"issue":"INSTRUMENT_DECLINED"}]}""",
        };
        var (sut, _) = CreateSut(handler);

        Assert.IsFalse(await sut.ProcessPaymentAsync(OrderId));
    }

    [TestMethod]
    public async Task ProcessPaymentAsync_WhenCaptureReturns500_ReturnsFalse()
    {
        var handler = new MockPayPalHandler { CaptureHttpStatus = HttpStatusCode.InternalServerError, CaptureBody = "{}" };
        var (sut, _) = CreateSut(handler);

        Assert.IsFalse(await sut.ProcessPaymentAsync(OrderId));
    }

    [TestMethod]
    public async Task ProcessPaymentAsync_WhenCaptureBodyMalformed_ReturnsFalseWithoutThrowing()
    {
        var handler = new MockPayPalHandler { CaptureBody = "<<<not-json>>>" };
        var (sut, _) = CreateSut(handler);

        Assert.IsFalse(await sut.ProcessPaymentAsync(OrderId));
    }

    // ---------- Authorization state guards ----------

    [TestMethod]
    public async Task ProcessPaymentAsync_WhenAuthorizationAlreadyVoided_ReturnsFalseAndDoesNotCapture()
    {
        var handler = new MockPayPalHandler { AuthorizationStatus = "VOIDED" };
        var (sut, _) = CreateSut(handler);

        Assert.IsFalse(await sut.ProcessPaymentAsync(OrderId));
        Assert.IsNull(handler.LastRequestTo("/capture"), "Capture must not be attempted on a voided authorization.");
    }

    [TestMethod]
    public async Task ProcessPaymentAsync_WhenGetAuthorizationReturns404_ReturnsFalse()
    {
        var handler = new MockPayPalHandler
        {
            AuthorizationHttpStatus = HttpStatusCode.NotFound,
            AuthorizationBody = """{"name":"RESOURCE_NOT_FOUND","message":"Not found","debug_id":"d2"}""",
        };
        var (sut, _) = CreateSut(handler);

        Assert.IsFalse(await sut.ProcessPaymentAsync(OrderId));
    }

    // ---------- UC3 re-authorization ----------

    [TestMethod]
    public async Task ProcessPaymentAsync_WhenAuthorizationPastHonorWindow_ReauthorizesThenCaptures()
    {
        var handler = new MockPayPalHandler { AuthorizationCreatedDaysAgo = 5 }; // past 3-day honor window
        var (sut, ordering) = CreateSut(handler);

        var result = await sut.ProcessPaymentAsync(OrderId);

        Assert.IsTrue(result);

        var reauth = handler.LastRequestTo("/reauthorize");
        Assert.IsNotNull(reauth, "A re-authorization should have been issued.");
        Assert.AreEqual($"reauth-{OrderId}-{AuthorizationId}", reauth!.PayPalRequestId);

        // The refreshed authorization id travels with the order, and capture uses it.
        await ordering.Received(1).UpdatePayPalReferencesAsync(OrderId, "AUTH-REAUTH", null, Arg.Any<CancellationToken>());
        var capture = handler.LastRequestTo("/capture");
        Assert.AreEqual($"capture-{OrderId}-AUTH-REAUTH", capture!.PayPalRequestId);
    }

    [TestMethod]
    public async Task ProcessPaymentAsync_WhenAuthorizationPastValidityWindow_ReturnsFalseWithoutCapture()
    {
        var handler = new MockPayPalHandler { AuthorizationCreatedDaysAgo = 30 }; // past 29-day validity
        var (sut, _) = CreateSut(handler);

        Assert.IsFalse(await sut.ProcessPaymentAsync(OrderId));
        Assert.IsNull(handler.LastRequestTo("/reauthorize"));
        Assert.IsNull(handler.LastRequestTo("/capture"));
    }

    // ---------- Idempotency (K5) ----------

    [TestMethod]
    public async Task ProcessPaymentAsync_CalledTwice_UsesSameStableIdempotencyKey()
    {
        var handler = new MockPayPalHandler();
        var (sut, _) = CreateSut(handler);

        Assert.IsTrue(await sut.ProcessPaymentAsync(OrderId));
        Assert.IsTrue(await sut.ProcessPaymentAsync(OrderId));

        var captureKeys = handler.RequestsTo("/capture").Select(r => r.PayPalRequestId).Distinct().ToList();
        Assert.IsTrue(captureKeys.Count == 1, "Retries must reuse one stable PayPal-Request-Id so PayPal de-duplicates the capture.");
        Assert.AreEqual($"capture-{OrderId}-{AuthorizationId}", captureKeys[0]);
    }

    // ---------- Cancellation (K6) ----------

    [TestMethod]
    public async Task ProcessPaymentAsync_WhenCallerCancels_DoesNotCapture()
    {
        var handler = new MockPayPalHandler();
        var (sut, _) = CreateSut(handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Cancellation must be honored: no successful capture, regardless of how it surfaces.
        var captured = false;
        try
        {
            captured = await sut.ProcessPaymentAsync(OrderId, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Acceptable: caller cancellation propagates.
        }

        Assert.IsFalse(captured);
        Assert.IsNull(handler.LastRequestTo("/capture"), "No capture should occur once the caller has cancelled.");
    }

    // ---------- Kill switch / not configured ----------

    [TestMethod]
    public async Task ProcessPaymentAsync_WhenPayPalDisabled_FallsBackToSimulatedFlagWithoutCallingPayPal()
    {
        var handler = new MockPayPalHandler();
        var options = NewOptions();
        options.UsePayPal = false;
        options.PaymentSucceeded = true;
        var (sut, _) = CreateSut(handler, options);

        Assert.IsTrue(await sut.ProcessPaymentAsync(OrderId));
        Assert.IsTrue(handler.AllRequests.Count == 0, "No PayPal call should be made when PayPal is disabled.");
    }

    // ---------- Void (UC4/UC5) ----------

    [TestMethod]
    public async Task VoidAuthorizationAsync_WhenSuccessful_SendsVoidWithIdempotencyKey()
    {
        var handler = new MockPayPalHandler { VoidHttpStatus = HttpStatusCode.NoContent };
        var (sut, _) = CreateSut(handler);

        await sut.VoidAuthorizationAsync(OrderId);

        var voidReq = handler.LastRequestTo("/void");
        Assert.IsNotNull(voidReq);
        Assert.AreEqual($"void-{OrderId}-{AuthorizationId}", voidReq!.PayPalRequestId);
    }

    [TestMethod]
    public async Task VoidAuthorizationAsync_WhenAlreadyVoided422_DoesNotThrow()
    {
        var handler = new MockPayPalHandler
        {
            VoidHttpStatus = (HttpStatusCode)422,
            VoidBody = """{"name":"UNPROCESSABLE_ENTITY","message":"Already voided","debug_id":"d3","details":[{"issue":"AUTHORIZATION_VOIDED"}]}""",
        };
        var (sut, _) = CreateSut(handler);

        // Must complete without throwing: a void failure never blocks eShop's cancellation.
        await sut.VoidAuthorizationAsync(OrderId);
    }

    // ---------- helpers ----------

    private static PaymentOptions NewOptions() => new()
    {
        UsePayPal = true,
        PayPalClientId = "test-client-id",
        PayPalClientSecret = "test-secret",
        PayPalEnvironment = "Sandbox",
        CurrencyCode = "USD",
        CurrencyDecimalPlaces = 2,
        HonorWindowDays = 3,
        ValidityWindowDays = 29,
        PerAttemptTimeoutSeconds = 10,
        TotalTimeoutSeconds = 30,
        PaymentSucceeded = true,
    };

    private static (PayPalPaymentService Sut, IOrderingApiClient Ordering) CreateSut(
        MockPayPalHandler handler, PaymentOptions? options = null)
    {
        options ??= NewOptions();

        var ordering = Substitute.For<IOrderingApiClient>();
        ordering.GetOrderAsync(OrderId, Arg.Any<CancellationToken>()).Returns(new OrderDto
        {
            OrderNumber = OrderId,
            Total = 99.99m,
            PayPalOrderId = "ORDER-1",
            PayPalAuthorizationId = AuthorizationId,
        });

        var optionsMonitor = Substitute.For<IOptionsMonitor<PaymentOptions>>();
        optionsMonitor.CurrentValue.Returns(options);

        var logger = Substitute.For<ILogger<PayPalPaymentService>>();

        var client = PayPalSdkClientFactory.Create(options, new HttpClient(handler));
        var sut = new PayPalPaymentService(client, ordering, optionsMonitor, logger);
        return (sut, ordering);
    }

    /// <summary>
    /// Routes PayPal calls (OAuth token, get/reauthorize/capture/void authorization) to configurable
    /// responses and records each request so tests can assert the outgoing shape.
    /// </summary>
    private sealed class MockPayPalHandler : HttpMessageHandler
    {
        public string AuthorizationStatus { get; set; } = "CREATED";
        public int AuthorizationCreatedDaysAgo { get; set; } = 1;
        public HttpStatusCode AuthorizationHttpStatus { get; set; } = HttpStatusCode.OK;
        public string? AuthorizationBody { get; set; }

        public string CaptureStatus { get; set; } = "COMPLETED";
        public HttpStatusCode CaptureHttpStatus { get; set; } = HttpStatusCode.OK;
        public string? CaptureBody { get; set; }

        public HttpStatusCode VoidHttpStatus { get; set; } = HttpStatusCode.NoContent;
        public string? VoidBody { get; set; }

        public List<RecordedRequest> AllRequests { get; } = new();

        public IEnumerable<RecordedRequest> RequestsTo(string pathFragment) =>
            AllRequests.Where(r => r.Path.Contains(pathFragment, StringComparison.OrdinalIgnoreCase));

        public RecordedRequest? LastRequestTo(string pathFragment) =>
            RequestsTo(pathFragment).LastOrDefault();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

            request.Headers.TryGetValues("PayPal-Request-Id", out var idValues);
            request.Headers.TryGetValues("Prefer", out var preferValues);
            AllRequests.Add(new RecordedRequest(
                request.Method.Method, path,
                idValues?.FirstOrDefault(), preferValues?.FirstOrDefault(), body));

            HttpResponseMessage response;

            // OAuth client-credentials token endpoint.
            if (path.Contains("oauth2/token", StringComparison.OrdinalIgnoreCase))
            {
                response = Json(HttpStatusCode.OK, """{"access_token":"test-token","token_type":"Bearer","expires_in":32400}""");
            }
            else if (path.EndsWith("/capture", StringComparison.OrdinalIgnoreCase))
            {
                response = Json(CaptureHttpStatus, CaptureBody ??
                    "{\"id\":\"CAP-1\",\"status\":\"" + CaptureStatus + "\",\"amount\":{\"currency_code\":\"USD\",\"value\":\"99.99\"}}");
            }
            else if (path.EndsWith("/reauthorize", StringComparison.OrdinalIgnoreCase))
            {
                response = Json(HttpStatusCode.OK, """{"id":"AUTH-REAUTH","status":"CREATED","amount":{"currency_code":"USD","value":"99.99"}}""");
            }
            else if (path.EndsWith("/void", StringComparison.OrdinalIgnoreCase))
            {
                response = VoidBody is null
                    ? new HttpResponseMessage(VoidHttpStatus)
                    : Json(VoidHttpStatus, VoidBody);
            }
            else if (path.Contains("/payments/authorizations/", StringComparison.OrdinalIgnoreCase))
            {
                // GET authorization details.
                var createTime = DateTimeOffset.UtcNow.AddDays(-AuthorizationCreatedDaysAgo).ToString("yyyy-MM-ddTHH:mm:ssZ");
                response = Json(AuthorizationHttpStatus, AuthorizationBody ??
                    "{\"id\":\"" + AuthorizationId + "\",\"status\":\"" + AuthorizationStatus +
                    "\",\"create_time\":\"" + createTime + "\",\"amount\":{\"currency_code\":\"USD\",\"value\":\"99.99\"}}");
            }
            else
            {
                response = new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            // HttpClient normally sets this; a fake handler must do it so the SDK's retry predicate
            // (which reads response.RequestMessage.Method) works exactly as in production.
            response.RequestMessage = request;
            return response;
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed record RecordedRequest(string Method, string Path, string? PayPalRequestId, string? Prefer, string? Body);
}
