using Microsoft.Extensions.Options;
using PaypalServerSdk.Standard;
using PaypalServerSdk.Standard.Authentication;

namespace eShop.WebApp.PayPal;

/// <summary>
/// DI wiring for the storefront PayPal checkout (UC1) backed by the PayPal Server SDK.
/// </summary>
public static class PayPalCheckoutExtensions
{
    /// <summary>
    /// Name of the DI-managed <see cref="HttpClient"/> handed to the PayPal SDK. WebApp uses the full
    /// <c>AddServiceDefaults()</c>, so this client inherits the standard resilience handler and
    /// OpenTelemetry HTTP instrumentation.
    /// </summary>
    public const string HttpClientName = "paypal-checkout";

    public static IHostApplicationBuilder AddPayPalCheckout(this IHostApplicationBuilder builder)
    {
        builder.Services.AddOptions<PayPalOptions>()
            .BindConfiguration("PayPal");

        builder.Services.AddHttpClient(HttpClientName);

        builder.Services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<PayPalOptions>>().Value;
            var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName);
            return CreateClient(options, httpClient);
        });

        return builder;
    }

    private static PaypalServerSdkClient CreateClient(PayPalOptions options, HttpClient httpClient)
    {
        var environment = options.IsProduction
            ? PaypalServerSdk.Standard.Environment.Production
            : PaypalServerSdk.Standard.Environment.Sandbox;

        return new PaypalServerSdkClient.Builder()
            .ClientCredentialsAuth(
                new ClientCredentialsAuthModel.Builder(
                    options.ClientId ?? string.Empty,
                    options.ClientSecret ?? string.Empty)
                .Build())
            .Environment(environment)
            .HttpClientConfig(config => config
                .HttpClientInstance(httpClient)
                .NumberOfRetries(0))
            .Build();
    }
}
