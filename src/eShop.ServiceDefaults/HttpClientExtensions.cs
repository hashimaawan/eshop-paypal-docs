using Microsoft.AspNetCore.Http;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace eShop.ServiceDefaults;

public static class HttpClientExtensions
{
    public static IHttpClientBuilder AddAuthToken(this IHttpClientBuilder builder)
    {
        builder.Services.AddHttpContextAccessor();

        builder.Services.TryAddTransient<HttpClientAuthorizationDelegatingHandler>();

        builder.AddHttpMessageHandler<HttpClientAuthorizationDelegatingHandler>();

        return builder;
    }

    /// <summary>
    /// Adds a delegating handler that attaches a bearer token obtained via OAuth2 client-credentials.
    /// Intended for background workers and service-to-service communication.
    /// </summary>
    public static IHttpClientBuilder AddClientCredentialsToken(this IHttpClientBuilder builder, string configurationSectionName = "ServiceAuth")
    {
        builder.Services.TryAddSingleton<ClientCredentialsTokenProvider>();
        builder.Services.TryAddSingleton(new ClientCredentialsTokenProvider.OptionsSection(configurationSectionName));
        builder.Services.TryAddTransient<ClientCredentialsTokenDelegatingHandler>();

        builder.AddHttpMessageHandler<ClientCredentialsTokenDelegatingHandler>();

        return builder;
    }

    private class HttpClientAuthorizationDelegatingHandler : DelegatingHandler
    {
        private readonly IHttpContextAccessor _httpContextAccessor;

        public HttpClientAuthorizationDelegatingHandler(IHttpContextAccessor httpContextAccessor)
        {
            _httpContextAccessor = httpContextAccessor;
        }

        public HttpClientAuthorizationDelegatingHandler(IHttpContextAccessor httpContextAccessor, HttpMessageHandler innerHandler) : base(innerHandler)
        {
            _httpContextAccessor = httpContextAccessor;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_httpContextAccessor.HttpContext is HttpContext context)
            {
                var accessToken = await context.GetTokenAsync("access_token");

                if (accessToken is not null)
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                }
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class ClientCredentialsTokenDelegatingHandler : DelegatingHandler
    {
        private readonly ClientCredentialsTokenProvider _tokenProvider;

        public ClientCredentialsTokenDelegatingHandler(ClientCredentialsTokenProvider tokenProvider)
        {
            _tokenProvider = tokenProvider;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Don't override an explicit Authorization header.
            if (request.Headers.Authorization is null)
            {
                var token = await _tokenProvider.GetAccessTokenAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(token))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                }
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class ClientCredentialsTokenProvider
    {
        public sealed record OptionsSection(string Name);

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;
        private readonly ILogger<ClientCredentialsTokenProvider> _logger;
        private readonly OptionsSection _optionsSection;
        private readonly SemaphoreSlim _lock = new(1, 1);

        private string? _accessToken;
        private DateTimeOffset _expiresAtUtc;

        public ClientCredentialsTokenProvider(
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            ILogger<ClientCredentialsTokenProvider> logger,
            OptionsSection optionsSection)
        {
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
            _logger = logger;
            _optionsSection = optionsSection;
        }

        public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken)
        {
            // Small clock skew buffer.
            if (_accessToken is not null && _expiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(1))
            {
                return _accessToken;
            }

            await _lock.WaitAsync(cancellationToken);
            try
            {
                if (_accessToken is not null && _expiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(1))
                {
                    return _accessToken;
                }

                var section = _configuration.GetSection(_optionsSection.Name);
                var authority = section["Authority"];
                var clientId = section["ClientId"];
                var clientSecret = section["ClientSecret"];
                var scope = section["Scope"];

                if (string.IsNullOrWhiteSpace(authority) ||
                    string.IsNullOrWhiteSpace(clientId) ||
                    string.IsNullOrWhiteSpace(clientSecret))
                {
                    // Not configured - skip attaching a token.
                    return null;
                }

                // IdentityServer/Duende standard token endpoint.
                var tokenEndpoint = $"{authority.TrimEnd('/')}/connect/token";

                var client = _httpClientFactory.CreateClient();

                using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["grant_type"] = "client_credentials",
                        ["client_id"] = clientId,
                        ["client_secret"] = clientSecret,
                        ["scope"] = string.IsNullOrWhiteSpace(scope) ? string.Empty : scope
                    })
                };

                using var response = await client.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogWarning(
                        "Client-credentials token request failed. StatusCode: {StatusCode}. Response: {ResponseBody}",
                        (int)response.StatusCode,
                        body);
                    return null;
                }

                var payload = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: cancellationToken);
                if (payload?.AccessToken is null || payload.ExpiresIn <= 0)
                {
                    _logger.LogWarning("Client-credentials token response was missing required fields.");
                    return null;
                }

                _accessToken = payload.AccessToken;
                _expiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(payload.ExpiresIn);

                return _accessToken;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to acquire client-credentials access token.");
                return null;
            }
            finally
            {
                _lock.Release();
            }
        }

        private sealed class TokenResponse
        {
            [JsonPropertyName("access_token")]
            public string? AccessToken { get; set; }

            [JsonPropertyName("expires_in")]
            public int ExpiresIn { get; set; }
        }
    }

}
