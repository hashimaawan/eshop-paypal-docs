using System.Globalization;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using PaypalServerSdk.Standard;
using PaypalServerSdk.Standard.Exceptions;
using PaypalServerSdk.Standard.Http.Response;
using PaypalServerSdk.Standard.Models;

namespace eShop.WebApp.PayPal;

/// <summary>
/// UC1 — checkout with PayPal approval + authorization hold. Creates a PayPal order with AUTHORIZE
/// intent, redirects the shopper to PayPal to approve, then authorizes the approved order (places the
/// fund hold). All PayPal interactions go through the PayPal Server SDK's OrdersController.
/// </summary>
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
        IOptions<PayPalOptions> optionsAccessor,
        PaypalServerSdkClient payPalClient,
        BasketPricingService basketPricingService,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("PayPalCreateOrder");
        var options = optionsAccessor.Value;

        var total = await basketPricingService.GetBasketTotalAsync(httpContext.RequestAborted);
        if (total <= 0)
        {
            return Results.BadRequest("Basket is empty.");
        }

        // E2E test mode: bypass real PayPal API and go straight to /paypal/return.
        if (options.E2ETestMode)
        {
            logger.LogInformation("E2E test mode: skipping PayPal order creation.");
            var fakeOrderId = "e2e-test-" + Guid.NewGuid().ToString("N");
            httpContext.Session.SetString(PayPalSessionKeys.OrderId, fakeOrderId);
            return Results.Redirect("/paypal/return?token=" + Uri.EscapeDataString(fakeOrderId));
        }

        if (!options.IsConfigured)
        {
            return Results.BadRequest("PayPal is not configured.");
        }

        // Create order with AUTHORIZE intent — funds are held, not taken, until capture at stock-confirmed.
        var shipping = BuildShippingFromClaims(httpContext.User);
        var createOrderInput = new CreateOrderInput
        {
            Body = new OrderRequest
            {
                Intent = CheckoutPaymentIntent.Authorize,
                PurchaseUnits = new List<PurchaseUnitRequest>
                {
                    new PurchaseUnitRequest
                    {
                        // §3.3: a single item total — eShop models no separate tax/shipping lines.
                        Amount = new AmountWithBreakdown
                        {
                            CurrencyCode = options.CurrencyCode,
                            MValue = FormatAmount(total, options),
                        },
                        Shipping = shipping,
                    },
                },
                ApplicationContext = new OrderApplicationContext
                {
                    ReturnUrl = options.RedirectUri,
                    CancelUrl = options.CancelUrl,
                    ShippingPreference = shipping is null
                        ? OrderApplicationContextShippingPreference.GetFromFile
                        : OrderApplicationContextShippingPreference.SetProvidedAddress,
                },
            },
        };

        Order order;
        try
        {
            ApiResponse<Order> response = await payPalClient.OrdersController.CreateOrderAsync(createOrderInput, httpContext.RequestAborted);
            order = response.Data;
        }
        catch (ApiException ex)
        {
            logger.LogError("Error creating PayPal order: {Status} {Reason}", ex.ResponseCode, DescribeError(ex));
            return Results.Problem("Unable to start PayPal payment.");
        }

        if (order is null || string.IsNullOrWhiteSpace(order.Id))
        {
            logger.LogError("Invalid PayPal order response: missing order id.");
            return Results.Problem("Unable to start PayPal payment.");
        }

        // Store the order id in session; validated when the shopper returns from PayPal.
        httpContext.Session.SetString(PayPalSessionKeys.OrderId, order.Id);

        var approveLink = order.Links?.FirstOrDefault(l => string.Equals(l.Rel, "approve", StringComparison.OrdinalIgnoreCase))?.Href;
        if (string.IsNullOrWhiteSpace(approveLink))
        {
            logger.LogError("No approval link in PayPal order response for order {OrderId}.", order.Id);
            return Results.Problem("Unable to start PayPal payment.");
        }

        return Results.Redirect(approveLink);
    }

    // UC1 step 2: shopper returns from PayPal having approved the payment.
    // We authorize here to place the fund hold, then redirect to checkout.
    private static async Task<IResult> AuthorizeOrderAsync(
        HttpContext httpContext,
        IOptions<PayPalOptions> optionsAccessor,
        PaypalServerSdkClient payPalClient,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("PayPalAuthorizeOrder");
        var options = optionsAccessor.Value;

        var orderId = httpContext.Request.Query["token"].ToString();
        if (string.IsNullOrWhiteSpace(orderId))
        {
            return Results.BadRequest("Missing PayPal order token.");
        }

        // Validate token against what we stored in session at order-creation time.
        var sessionOrderId = httpContext.Session.GetString(PayPalSessionKeys.OrderId);
        if (!string.Equals(orderId, sessionOrderId, StringComparison.Ordinal))
        {
            logger.LogWarning("PayPal return token does not match session.");
            return Results.Redirect("/checkout?error=payment_mismatch");
        }

        // E2E test mode: skip real authorize call.
        if (options.E2ETestMode)
        {
            var fakeAuthId = "e2e-auth-" + Guid.NewGuid().ToString("N");
            httpContext.Session.SetString(PayPalSessionKeys.AuthorizationId, fakeAuthId);
            logger.LogInformation("E2E test mode: fake authorization {AuthId} for order {OrderId}.", fakeAuthId, orderId);
            return Results.Redirect(
                $"/checkout?paid=1&paypalOrderId={Uri.EscapeDataString(orderId)}&paypalAuthorizationId={Uri.EscapeDataString(fakeAuthId)}");
        }

        if (!options.IsConfigured)
        {
            return Results.Problem("PayPal is not configured.");
        }

        var authorizeInput = new AuthorizeOrderInput
        {
            Id = orderId,
            // representation is required so the nested authorization id is returned.
            Prefer = "return=representation",
            // §3.2 idempotency: a retry of the same approved order never authorizes twice.
            PaypalRequestId = $"authorize-{orderId}",
        };

        OrderAuthorizeResponse authorization;
        try
        {
            ApiResponse<OrderAuthorizeResponse> response = await payPalClient.OrdersController.AuthorizeOrderAsync(authorizeInput, httpContext.RequestAborted);
            authorization = response.Data;
        }
        catch (ApiException ex)
        {
            logger.LogError("Error authorizing PayPal order {OrderId}: {Status} {Reason}", orderId, ex.ResponseCode, DescribeError(ex));
            return Results.Redirect("/checkout?error=authorization_failed");
        }

        var authorizationId = authorization?.PurchaseUnits?
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

    // Builds a PayPal shipping object from the address claims eShop already collects. Returns null
    // (so the shipping block is omitted) when a valid 2-letter country code can't be determined,
    // since PayPal rejects an address without one.
    private static ShippingDetails? BuildShippingFromClaims(ClaimsPrincipal user)
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

        var shipping = new ShippingDetails
        {
            Address = new Address
            {
                AddressLine1 = street,
                AdminArea2 = city ?? string.Empty,
                AdminArea1 = state ?? string.Empty,
                PostalCode = zip ?? string.Empty,
                CountryCode = countryCode,
            },
        };

        var fullName = user.FindFirst("name")?.Value ?? user.Identity?.Name;
        if (!string.IsNullOrWhiteSpace(fullName))
        {
            shipping.Name = new ShippingName { FullName = fullName };
        }

        return shipping;
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

    // Format money exactly to the currency's decimal places, rounding away from zero, invariant culture.
    private static string FormatAmount(decimal amount, PayPalOptions options)
    {
        var rounded = Math.Round(amount, options.CurrencyDecimalPlaces, MidpointRounding.AwayFromZero);
        return rounded.ToString("F" + options.CurrencyDecimalPlaces, CultureInfo.InvariantCulture);
    }

    // Non-secret, structured description of a PayPal error for logging (no tokens/PII).
    private static string DescribeError(ApiException ex)
    {
        if (ex is ErrorException error)
        {
            var issue = error.Details is { Count: > 0 } ? error.Details[0].Issue : null;
            return $"{error.Name}/{issue} (debug_id={error.DebugId})";
        }

        return ex.Message;
    }
}
