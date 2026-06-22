using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using PaypalServerSdk.Standard;
using PaypalServerSdk.Standard.Authentication;

namespace eShop.PaymentProcessor;

/// <summary>
/// DI wiring for the PayPal Server SDK based payment service.
/// </summary>
public static class PayPalServiceExtensions
{
    /// <summary>
    /// Name of the DI-managed <see cref="HttpClient"/> handed to the PayPal SDK. Because
    /// PaymentProcessor calls the full <c>AddServiceDefaults()</c>, this client automatically
    /// inherits the standard resilience handler and OpenTelemetry HTTP instrumentation.
    /// </summary>
    public const string HttpClientName = "paypal";

    public static IServiceCollection AddPayPalPaymentService(this IServiceCollection services)
    {
        services.AddOptions<PaymentOptions>()
            .BindConfiguration(nameof(PaymentOptions))
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<PaymentOptions>, PaymentOptionsValidator>();

        services.AddHttpClient(HttpClientName);

        // Tune the inherited standard resilience handler's timeouts for the PayPal client from
        // PaymentOptions, so the per-attempt and total budgets are configuration-driven (C5).
        services.AddOptions<HttpStandardResilienceOptions>(HttpClientName)
            .Configure<IOptions<PaymentOptions>>((resilience, paymentOptions) =>
            {
                var o = paymentOptions.Value;
                var perAttempt = TimeSpan.FromSeconds(o.PerAttemptTimeoutSeconds);
                resilience.AttemptTimeout.Timeout = perAttempt;
                resilience.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(o.TotalTimeoutSeconds);

                // The standard options validator requires the breaker sampling window to be at
                // least twice the per-attempt timeout.
                var minSampling = TimeSpan.FromSeconds(o.PerAttemptTimeoutSeconds * 2);
                if (resilience.CircuitBreaker.SamplingDuration < minSampling)
                {
                    resilience.CircuitBreaker.SamplingDuration = minSampling;
                }
            });

        // The SDK client is long-lived (B1) and built once from the validated options. It reuses the
        // DI-managed HttpClient and lets the SDK manage the OAuth client-credentials token lifecycle.
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<PaymentOptions>>().Value;
            var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName);
            return PayPalSdkClientFactory.Create(options, httpClient);
        });

        services.AddScoped<IPaymentService, PayPalPaymentService>();

        return services;
    }
}

/// <summary>
/// Builds a configured <see cref="PaypalServerSdkClient"/>. Kept separate so unit tests can build the
/// same client against a stubbed <see cref="HttpClient"/> (fake message handler).
/// </summary>
public static class PayPalSdkClientFactory
{
    public static PaypalServerSdkClient Create(PaymentOptions options, HttpClient httpClient)
    {
        var environment = options.IsProduction
            ? PaypalServerSdk.Standard.Environment.Production
            : PaypalServerSdk.Standard.Environment.Sandbox;

        return new PaypalServerSdkClient.Builder()
            .ClientCredentialsAuth(
                new ClientCredentialsAuthModel.Builder(
                    options.PayPalClientId ?? string.Empty,
                    options.PayPalClientSecret ?? string.Empty)
                .Build())
            .Environment(environment)
            .HttpClientConfig(config => config
                // Hand the SDK eShop's DI-managed HttpClient so PayPal calls inherit the standard
                // resilience handler + OpenTelemetry instrumentation; disable the SDK's own retry
                // layer so transient failures are not retried twice.
                .HttpClientInstance(httpClient)
                .NumberOfRetries(0))
            .Build();
    }
}
