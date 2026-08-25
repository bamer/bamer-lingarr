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

            // Try releases first
            var response = await httpClient.GetAsync($"https://api.github.com/repos/{GitHubRepo}/releases/latest");
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var releaseResponse = JsonSerializer.Deserialize<GitHubReleaseResponse>(content,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (!string.IsNullOrEmpty(releaseResponse?.TagName))
                {
                    var cacheOptions = new MemoryCacheEntryOptions()
                        .SetAbsoluteExpiration(TimeSpan.FromHours(24));
                    _cache.Set(CacheKeyLatestVersion, releaseResponse.TagName, cacheOptions);
                    _logger.LogInformation("Retrieved latest version from GitHub releases: {Version}", releaseResponse.TagName);
                    return releaseResponse.TagName;
                }
            }

            // Fallback to tags if no releases
            var tagsResponse = await httpClient.GetAsync($"https://api.github.com/repos/{GitHubRepo}/tags");
            if (tagsResponse.IsSuccessStatusCode)
            {
                var tagsContent = await tagsResponse.Content.ReadAsStringAsync();
                var tags = JsonSerializer.Deserialize<List<GitHubTagResponse>>(tagsContent,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                var latestTag = tags?.FirstOrDefault()?.Name;
                if (!string.IsNullOrEmpty(latestTag))
                {
                    var cacheOptions = new MemoryCacheEntryOptions()
                        .SetAbsoluteExpiration(TimeSpan.FromHours(24));
                    _cache.Set(CacheKeyLatestVersion, latestTag, cacheOptions);
                    _logger.LogInformation("Retrieved latest version from GitHub tags: {Version}", latestTag);
                    return latestTag;
                }
            }

            _logger.LogWarning("No releases or tags found on GitHub for {Repo}", GitHubRepo);
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

    private class GitHubTagResponse
    {
        public string? Name { get; set; }
    }
}
