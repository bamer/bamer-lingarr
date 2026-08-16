using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lingarr.Core;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Models.Telemetry;
using Microsoft.Extensions.Caching.Memory;

namespace Lingarr.Server.Services;

public class LingarrApiService : ILingarrApiService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LingarrApiService> _logger;
    private readonly IMemoryCache _cache;
    private const string CacheKeyLatestVersion = "LingarrApi_LatestVersion";
    private const string GitHubRepo = "bamer/bamer-lingarr";

    public LingarrApiService(
        IHttpClientFactory httpClientFactory,
        ILogger<LingarrApiService> logger,
        IMemoryCache cache)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _cache = cache;
    }

    public async Task<string?> GetLatestVersion()
    {
        // Check cache first
        if (_cache.TryGetValue(CacheKeyLatestVersion, out string? cachedVersion))
        {
            _logger.LogDebug("Returning cached version information from GitHub");
            return cachedVersion;
        }

        try
        {
            var httpClient = _httpClientFactory.CreateClient();
            httpClient.DefaultRequestHeaders.Add("User-Agent", $"{LingarrVersion.Name}/{LingarrVersion.Number}");

            var response = await httpClient.GetAsync($"https://api.github.com/repos/{GitHubRepo}/releases/latest");

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to get latest version from GitHub: {StatusCode}",
                    response.StatusCode);
                return null;
            }

            var content = await response.Content.ReadAsStringAsync();
            var releaseResponse = JsonSerializer.Deserialize<GitHubReleaseResponse>(content,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

            var version = releaseResponse?.TagName;
            if (!string.IsNullOrEmpty(version))
            {
                var cacheOptions = new MemoryCacheEntryOptions()
                    .SetAbsoluteExpiration(TimeSpan.FromHours(24));
                _cache.Set(CacheKeyLatestVersion, version, cacheOptions);

                _logger.LogInformation("Retrieved latest version from GitHub: {Version}", version);
                return version;
            }

            _logger.LogWarning("GitHub release returned empty version");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch latest version from GitHub");
            return null;
        }
    }

    public async Task<bool> SubmitTelemetry(TelemetryPayload payload)
    {
        try
        {
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            var signature = GenerateHmac(json);
            var httpClient = _httpClientFactory.CreateClient();
            var baseUrl = new UriBuilder
            {
                Scheme = Uri.UriSchemeHttps,
                Host = $"api.{LingarrVersion.Name.ToLower()}.com"
            }.Uri.ToString();
            var request = new HttpRequestMessage(HttpMethod.Post, baseUrl)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            request.Headers.Add("X-Signature", signature);
            var response = await httpClient.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                return true;
            }
            _logger.LogWarning("Telemetry submission failed: {Status} - {Response}",
                response.StatusCode,
                await response.Content.ReadAsStringAsync());
            return false;

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to submit telemetry to Lingarr API");
            return false;
        }
    }

    private string GenerateHmac(string payload)
    {
        using var hmac = new HMACSHA256("tSBTCU4Qv76so0c2U8bBX0faSzc3uc6Z"u8.ToArray());
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    private class GitHubReleaseResponse
    {
        public string? TagName { get; set; }
    }
}
