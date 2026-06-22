using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;

namespace eShop.WebApp.PayPal;

public static class PayPalEndpoints
{
    public static void MapPayPalEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/paypal/pay", CreateOrderAndRedirectAsync);
        app.MapGet("/paypal/return", AuthorizeOrderAsync);
        app.MapGet("/paypal/cancel", CancelAsync);
    }

    // UC1 step 1: create a PayPal order with AUTHORIZE intent and redirect the shopper to PayPal.
    private static async Task<IResult> CreateOrderAndRedirectAsync(
        HttpContext httpContext,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        BasketPricingService basketPricingService,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("PayPalCreateOrder");
        var total = await basketPricingService.GetBasketTotalAsync(httpContext.RequestAborted);
        if (total <= 0)
        {
            return Results.BadRequest("Basket is empty.");
        }

        // E2E test mode: bypass real PayPal API and go straight to /paypal/return.
        if (configuration.GetValue<bool>("PayPal:E2ETestMode"))
        {
            logger.LogInformation("E2E test mode: skipping PayPal order creation.");
            var fakeOrderId = "e2e-test-" + Guid.NewGuid().ToString("N");
            httpContext.Session.SetString(PayPalSessionKeys.OrderId, fakeOrderId);
            return Results.Redirect("/paypal/return?token=" + Uri.EscapeDataString(fakeOrderId));
        }

        var env = configuration["PayPal:Environment"] ?? "Sandbox";
        var clientId = configuration["PayPal:ClientId"];
        var clientSecret = configuration["PayPal:ClientSecret"];
        var returnUrl = configuration["PayPal:RedirectUri"];
        var cancelUrl = configuration["PayPal:CancelUrl"];
        var currency = configuration["PayPal:CurrencyCode"] ?? "USD";

        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret) ||
            string.IsNullOrWhiteSpace(returnUrl) || string.IsNullOrWhiteSpace(cancelUrl))
        {
            return Results.BadRequest("PayPal is not configured.");
        }

        var baseUrl = env.Equals("Live", StringComparison.OrdinalIgnoreCase)
            ? "https://api-m.paypal.com"
            : "https://api-m.sandbox.paypal.com";

        var client = httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(baseUrl);

        var accessToken = await GetAccessTokenAsync(client, clientId, clientSecret, logger, httpContext.RequestAborted);
        if (accessToken is null)
            return Results.Problem("Unable to start PayPal payment.");

        // Create order with AUTHORIZE intent — funds are held, not taken, until capture at stock-confirmed.
        using var orderReq = new HttpRequestMessage(HttpMethod.Post, "/v2/checkout/orders");
        orderReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var amount = new
        {
            currency_code = currency,
            value = total.ToString("F2", CultureInfo.InvariantCulture)
        };

        // UC1/§3.3: pass the shipping address eShop collected so the shopper sees
        // consistent details at PayPal. Omitted gracefully if we can't form a valid address.
        var shipping = BuildShippingFromClaims(httpContext.User);
        object purchaseUnit = shipping is null
            ? new { amount }
            : new { amount, shipping };

        orderReq.Content = JsonContent.Create(new
        {
            intent = "AUTHORIZE",
            purchase_units = new[] { purchaseUnit },
            application_context = new
            {
                return_url = returnUrl,
                cancel_url = cancelUrl
            }
        });

        var orderResp = await client.SendAsync(orderReq, httpContext.RequestAborted);
        if (!orderResp.IsSuccessStatusCode)
        {
            var body = await orderResp.Content.ReadAsStringAsync();
            logger.LogError("Error creating PayPal order: {Status} {Body}", orderResp.StatusCode, body);
            return Results.Problem("Unable to start PayPal payment.");
        }

        var order = await orderResp.Content.ReadFromJsonAsync<PayPalOrderResponse>(cancellationToken: httpContext.RequestAborted);
        if (order is null || string.IsNullOrWhiteSpace(order.Id))
        {
            logger.LogError("Invalid PayPal order response: missing order id.");
            return Results.Problem("Unable to start PayPal payment.");
        }

        // Store the order id in session; validated when the shopper returns from PayPal.
        httpContext.Session.SetString(PayPalSessionKeys.OrderId, order.Id);

        var approveLink = order.Links?.FirstOrDefault(l => l.Rel == "approve")?.Href;
        if (string.IsNullOrWhiteSpace(approveLink))
        {
            logger.LogError("No approval link in PayPal order response.");
            return Results.Problem("Unable to start PayPal payment.");
        }

        return Results.Redirect(approveLink);
    }

    // UC1 step 2: shopper returns from PayPal having approved the payment.
    // We call /authorize here to place the fund hold, then redirect to checkout.
    private static async Task<IResult> AuthorizeOrderAsync(
        HttpContext httpContext,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("PayPalAuthorizeOrder");
        var orderId = httpContext.Request.Query["token"].ToString();
        if (string.IsNullOrWhiteSpace(orderId))
            return Results.BadRequest("Missing PayPal order token.");

        // Validate token against what we stored in session at order-creation time.
        var sessionOrderId = httpContext.Session.GetString(PayPalSessionKeys.OrderId);
        if (!string.Equals(orderId, sessionOrderId, StringComparison.Ordinal))
        {
            logger.LogWarning("PayPal return token does not match session. token={Token}", orderId);
            return Results.Redirect("/checkout?error=payment_mismatch");
        }

        // E2E test mode: skip real authorize call.
        if (configuration.GetValue<bool>("PayPal:E2ETestMode"))
        {
            var fakeAuthId = "e2e-auth-" + Guid.NewGuid().ToString("N");
            httpContext.Session.SetString(PayPalSessionKeys.AuthorizationId, fakeAuthId);
            logger.LogInformation("E2E test mode: fake authorization {AuthId} for order {OrderId}.", fakeAuthId, orderId);
            return Results.Redirect(
                $"/checkout?paid=1&paypalOrderId={Uri.EscapeDataString(orderId)}&paypalAuthorizationId={Uri.EscapeDataString(fakeAuthId)}");
        }

        var env = configuration["PayPal:Environment"] ?? "Sandbox";
        var clientId = configuration["PayPal:ClientId"];
        var clientSecret = configuration["PayPal:ClientSecret"];

        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            return Results.Problem("PayPal is not configured.");

        var baseUrl = env.Equals("Live", StringComparison.OrdinalIgnoreCase)
            ? "https://api-m.paypal.com"
            : "https://api-m.sandbox.paypal.com";

        var client = httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(baseUrl);

        var accessToken = await GetAccessTokenAsync(client, clientId, clientSecret, logger, httpContext.RequestAborted);
        if (accessToken is null)
            return Results.Redirect("/checkout?error=payment_failed");

        // Authorize the shopper-approved order — places the fund hold.
        using var authReq = new HttpRequestMessage(HttpMethod.Post, $"/v2/checkout/orders/{orderId}/authorize");
        authReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        // §3.2 idempotency: a retry of the same approved order never authorizes twice.
        authReq.Headers.Add("PayPal-Request-Id", $"authorize-{orderId}");
        authReq.Content = JsonContent.Create(new { });

        var authResp = await client.SendAsync(authReq, httpContext.RequestAborted);
        if (!authResp.IsSuccessStatusCode)
        {
            var body = await authResp.Content.ReadAsStringAsync();
            logger.LogError("Error authorizing PayPal order {OrderId}: {Status} {Body}", orderId, authResp.StatusCode, body);
            return Results.Redirect("/checkout?error=authorization_failed");
        }

        var authData = await authResp.Content.ReadFromJsonAsync<PayPalAuthorizeOrderResponse>(cancellationToken: httpContext.RequestAborted);
        var authorizationId = authData?.PurchaseUnits?
            .FirstOrDefault()?.Payments?.Authorizations?
            .FirstOrDefault()?.Id;

        if (string.IsNullOrWhiteSpace(authorizationId))
        {
            logger.LogError("No authorization ID in PayPal authorize response for order {OrderId}.", orderId);
            return Results.Redirect("/checkout?error=authorization_failed");
        }

        // Store authorization ID in session; validated in checkout before placing the order.
        httpContext.Session.SetString(PayPalSessionKeys.AuthorizationId, authorizationId);

        logger.LogInformation("PayPal order {OrderId} authorized. AuthorizationId={AuthId}", orderId, authorizationId);

        return Results.Redirect(
            $"/checkout?paid=1&paypalOrderId={Uri.EscapeDataString(orderId)}&paypalAuthorizationId={Uri.EscapeDataString(authorizationId)}");
    }

    // UC1 abort: shopper cancelled at PayPal — no authorization was placed.
    private static IResult CancelAsync() => Results.Redirect("/checkout");

    // ---- helpers ----

    private static async Task<string?> GetAccessTokenAsync(
        HttpClient client, string clientId, string clientSecret,
        ILogger logger, CancellationToken ct)
    {
        var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{clientId}:{clientSecret}"));
        using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/oauth2/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials" })
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);

        var resp = await client.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            logger.LogError("Error getting PayPal access token: {Status} {Body}", resp.StatusCode, body);
            return null;
        }

        var token = await resp.Content.ReadFromJsonAsync<PayPalTokenResponse>(cancellationToken: ct);
        return string.IsNullOrWhiteSpace(token?.AccessToken) ? null : token.AccessToken;
    }

    // Builds a PayPal shipping object from the address claims eShop already collects.
    // Returns null (so the shipping block is omitted) when a valid 2-letter country
    // code can't be determined, since PayPal rejects an address without one.
    private static object? BuildShippingFromClaims(ClaimsPrincipal user)
    {
        string? Claim(string type) => user.Claims.FirstOrDefault(c => c.Type == type)?.Value;

        var street = Claim("address_street");
        var city = Claim("address_city");
        var state = Claim("address_state");
        var zip = Claim("address_zip_code");
        var countryCode = NormalizeCountryCode(Claim("address_country"));

        if (string.IsNullOrWhiteSpace(street) || string.IsNullOrWhiteSpace(countryCode))
        {
            return null;
        }

        var address = new
        {
            address_line_1 = street,
            admin_area_2 = city ?? string.Empty,
            admin_area_1 = state ?? string.Empty,
            postal_code = zip ?? string.Empty,
            country_code = countryCode
        };

        var fullName = user.FindFirst("name")?.Value ?? user.Identity?.Name;
        return string.IsNullOrWhiteSpace(fullName)
            ? new { address }
            : new { name = new { full_name = fullName }, address };
    }

    // eShop stores country as free text (e.g. "U.S."); PayPal needs ISO 3166 alpha-2.
    private static string? NormalizeCountryCode(string? country)
    {
        if (string.IsNullOrWhiteSpace(country)) return null;
        var c = country.Trim();

        if (c.Length == 2 && c.All(char.IsLetter)) return c.ToUpperInvariant();

        var normalized = c.Replace(".", string.Empty).Replace(" ", string.Empty).ToUpperInvariant();
        return normalized switch
        {
            "US" or "USA" or "UNITEDSTATES" or "UNITEDSTATESOFAMERICA" => "US",
            "UK" or "GB" or "UNITEDKINGDOM" or "GREATBRITAIN" => "GB",
            "CA" or "CANADA" => "CA",
            _ => null
        };
    }

    // ---- response DTOs ----

    private sealed class PayPalTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; init; } = string.Empty;
    }

    private sealed class PayPalOrderResponse
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("links")]
        public List<PayPalLink>? Links { get; init; }
    }

    private sealed class PayPalLink
    {
        [JsonPropertyName("rel")]
        public string Rel { get; init; } = string.Empty;

        [JsonPropertyName("href")]
        public string Href { get; init; } = string.Empty;
    }

    private sealed class PayPalAuthorizeOrderResponse
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("status")]
        public string? Status { get; init; }

        [JsonPropertyName("purchase_units")]
        public List<PayPalPurchaseUnit>? PurchaseUnits { get; init; }
    }

    private sealed class PayPalPurchaseUnit
    {
        [JsonPropertyName("payments")]
        public PayPalPayments? Payments { get; init; }
    }

    private sealed class PayPalPayments
    {
        [JsonPropertyName("authorizations")]
        public List<PayPalAuthorizationRef>? Authorizations { get; init; }
    }

    private sealed class PayPalAuthorizationRef
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("status")]
        public string? Status { get; init; }
    }
}
