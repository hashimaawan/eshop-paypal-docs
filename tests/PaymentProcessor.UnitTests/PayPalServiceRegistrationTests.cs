#nullable enable
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PaypalServerSdk.Standard;

namespace eShop.PaymentProcessor.UnitTests;

/// <summary>
/// Verifies the DI wiring added by <see cref="PayPalServiceExtensions.AddPayPalPaymentService"/>:
/// the SDK client constructs, the options validator fires, and the resilience configuration is valid
/// at startup — without requiring RabbitMQ/Aspire infrastructure.
/// </summary>
[TestClass]
public sealed class PayPalServiceRegistrationTests
{
    [TestMethod]
    public void Resolves_SdkClient_And_Service_When_Configured()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["PaymentOptions:UsePayPal"] = "true",
            ["PaymentOptions:PayPalClientId"] = "test-id",
            ["PaymentOptions:PayPalClientSecret"] = "test-secret",
            ["PaymentOptions:PayPalEnvironment"] = "Sandbox",
        });

        // Resolving the SDK client also builds the DI HttpClient pipeline (validates resilience options).
        Assert.IsNotNull(provider.GetRequiredService<PaypalServerSdkClient>());
        Assert.IsNotNull(provider.GetRequiredService<IPaymentService>());
        Assert.IsTrue(provider.GetRequiredService<IOptions<PaymentOptions>>().Value.IsPayPalConfigured);
    }

    [TestMethod]
    public void Resolves_When_Disabled_WithoutCredentials()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["PaymentOptions:UsePayPal"] = "false",
        });

        // In simulation mode the client still constructs (it is simply never invoked).
        Assert.IsNotNull(provider.GetRequiredService<PaypalServerSdkClient>());
        Assert.IsFalse(provider.GetRequiredService<IOptions<PaymentOptions>>().Value.IsPayPalConfigured);
    }

    [TestMethod]
    public void OptionsValidation_Fails_When_Enabled_Without_Secret()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["PaymentOptions:UsePayPal"] = "true",
            ["PaymentOptions:PayPalClientId"] = "test-id",
            // PayPalClientSecret deliberately omitted.
        });

        Assert.ThrowsExactly<OptionsValidationException>(
            () => { _ = provider.GetRequiredService<IOptions<PaymentOptions>>().Value; });
    }

    private static ServiceProvider BuildProvider(Dictionary<string, string?> config)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();

        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(configuration);
        services.AddLogging();
        services.AddSingleton(Substitute.For<IOrderingApiClient>());
        services.AddPayPalPaymentService();

        return services.BuildServiceProvider();
    }
}
