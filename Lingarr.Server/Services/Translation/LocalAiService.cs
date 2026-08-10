using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Lingarr.Contracts.Exceptions;
using Lingarr.Contracts.Models.Batch;
using Lingarr.Contracts.Translation;
using Lingarr.Core.Configuration;
using Lingarr.Server.Exceptions;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Models.Integrations.Translation;
using Lingarr.Server.Services.Translation.Base;

namespace Lingarr.Server.Services.Translation;

public class LocalAiService : BaseLanguageService, ITranslationService, IBatchTranslationService
{
    private readonly HttpClient _httpClient;
    private readonly IRequestTemplateService _requestTemplateService;
    private string? _model;
    private string? _endpoint;
    private string? _chatRequestTemplate;
    private string? _generateRequestTemplate;
    private bool _isChatEndpoint;
    private bool _initialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private Dictionary<string, object?> _modelOptions = new();

    /// <inheritdoc />
    public override string? ModelName => _model;

    // retry settings
    private int _maxRetries;
    private TimeSpan _retryDelay;
    private int _retryDelayMultiplier;

    public LocalAiService(
        ISettingService settings,
        HttpClient httpClient,
        ILogger<LocalAiService> logger,
        LanguageCodeService languageCodeService,
        IRequestTemplateService requestTemplateService)
        : base(settings, logger, languageCodeService)
    {
        _httpClient = httpClient;
        _requestTemplateService = requestTemplateService;
    }

    /// <summary>
    /// Initializes the translation service with necessary configurations and credentials.
    /// This method is thread-safe and ensures one-time initialization of service dependencies.
    /// </summary>
    /// <param name="sourceLanguage">The source language code for translation</param>
    /// <param name="targetLanguage">The target language code for translation</param>
    /// <returns>A task that represents the asynchronous initialization operation</returns>
    /// <exception cref="InvalidOperationException">Thrown when required configuration settings are missing or invalid</exception>
    private async Task InitializeAsync(string sourceLanguage, string targetLanguage)
    {
        if (_initialized) return;

        await _initLock.WaitAsync();
        try
        {
            if (_initialized) return;

            var settings = await _settings.GetSettings([
                SettingKeys.Translation.LocalAi.Model,
                SettingKeys.Translation.LocalAi.Endpoint,
                SettingKeys.Translation.LocalAi.ChatRequestTemplate,
                SettingKeys.Translation.LocalAi.GenerateRequestTemplate,
                SettingKeys.Translation.AiPrompt,
                SettingKeys.Translation.AiUserPrompt,
                SettingKeys.Translation.RequestTimeout,
                SettingKeys.Translation.MaxRetries,
                SettingKeys.Translation.RetryDelay,
                SettingKeys.Translation.RetryDelayMultiplier,
                SettingKeys.Translation.LanguageCodeFormat,
                SettingKeys.Translation.ModelTemperature,
                SettingKeys.Translation.ModelTopP,
                SettingKeys.Translation.ModelMaxTokens,
                SettingKeys.Translation.ModelReasoningBudget,
                SettingKeys.Translation.ModelChatTemplateKwargs,
                SettingKeys.Translation.ModelReasoningEffort
            ]);
            _model = settings[SettingKeys.Translation.LocalAi.Model];
            _endpoint = settings[SettingKeys.Translation.LocalAi.Endpoint];
            _chatRequestTemplate = !string.IsNullOrEmpty(settings[SettingKeys.Translation.LocalAi.ChatRequestTemplate])
                ? settings[SettingKeys.Translation.LocalAi.ChatRequestTemplate]
                : _requestTemplateService.GetDefaultTemplate(SettingKeys.Translation.LocalAi.ChatRequestTemplate);
            _generateRequestTemplate = !string.IsNullOrEmpty(settings[SettingKeys.Translation.LocalAi.GenerateRequestTemplate])
                ? settings[SettingKeys.Translation.LocalAi.GenerateRequestTemplate]
                : _requestTemplateService.GetDefaultTemplate(SettingKeys.Translation.LocalAi.GenerateRequestTemplate);

            if (string.IsNullOrEmpty(_model) || string.IsNullOrEmpty(_endpoint))
            {
                throw new InvalidOperationException("Local AI service requires both endpoint address and model name to be configured in settings.");
            }

            SetLanguageReplacements(sourceLanguage, targetLanguage, settings[SettingKeys.Translation.LanguageCodeFormat]);
            _prompt = settings[SettingKeys.Translation.AiPrompt];
            _userPrompt = settings[SettingKeys.Translation.AiUserPrompt];

            // Normalize endpoint URLs — append path if only base URL is provided
            _endpoint = NormalizeEndpoint(_endpoint);
            _isChatEndpoint = _endpoint.Contains("completions", StringComparison.OrdinalIgnoreCase);

            var requestTimeout = int.TryParse(settings[SettingKeys.Translation.RequestTimeout],
                out var timeOut)
                ? timeOut
                : 5;
            _httpClient.Timeout = TimeSpan.FromMinutes(requestTimeout);
            _httpClient.DefaultRequestHeaders.Accept.Clear();
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");

            var apiKey = await _settings.GetEncryptedSetting(SettingKeys.Translation.LocalAi.ApiKey);
            if (!string.IsNullOrEmpty(apiKey))
            {
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            _maxRetries = int.TryParse(settings[SettingKeys.Translation.MaxRetries], out var maxRetries) 
                ? maxRetries 
                : 5;
            var retryDelaySeconds = int.TryParse(settings[SettingKeys.Translation.RetryDelay], out var delaySeconds) 
                ? delaySeconds 
                : 1;
            _retryDelay = TimeSpan.FromSeconds(retryDelaySeconds);
            _retryDelayMultiplier = int.TryParse(settings[SettingKeys.Translation.RetryDelayMultiplier], out var multiplier) 
                ? multiplier 
                : 2;

            // Build model options — only include non-empty values
            _modelOptions = new Dictionary<string, object?>();
            if (settings.TryGetValue(SettingKeys.Translation.ModelTemperature, out var tempStr) &&
                double.TryParse(tempStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var temperature))
                _modelOptions["temperature"] = temperature;
            if (settings.TryGetValue(SettingKeys.Translation.ModelTopP, out var topPStr) &&
                double.TryParse(topPStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var topP))
                _modelOptions["top_p"] = topP;
            if (settings.TryGetValue(SettingKeys.Translation.ModelMaxTokens, out var maxTokensStr) &&
                int.TryParse(maxTokensStr, out var maxTokens))
                _modelOptions["max_tokens"] = maxTokens;
            if (settings.TryGetValue(SettingKeys.Translation.ModelReasoningBudget, out var rbStr) &&
                int.TryParse(rbStr, out var reasoningBudget))
                _modelOptions["reasoning_budget"] = reasoningBudget;
            if (settings.TryGetValue(SettingKeys.Translation.ModelChatTemplateKwargs, out var kwargsStr) &&
                !string.IsNullOrWhiteSpace(kwargsStr))
                _modelOptions["chat_template_kwargs"] = kwargsStr;
            if (settings.TryGetValue(SettingKeys.Translation.ModelReasoningEffort, out var effortStr) &&
                !string.IsNullOrWhiteSpace(effortStr))
                _modelOptions["reasoning_effort"] = effortStr;

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <inheritdoc />
    public override async Task<string> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        List<string>? contextLinesBefore,
        List<string>? contextLinesAfter,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(sourceLanguage, targetLanguage);

        var replacements = GetReplacements(_model!, text, contextLinesBefore, contextLinesAfter);
        using var retry = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, retry.Token);

        var delay = _retryDelay;
        for (var attempt = 1; attempt <= _maxRetries; attempt++)
        {
            try
            {
                return _isChatEndpoint
                    ? await TranslateWithChatApi(replacements, retry.Token)
                    : await TranslateWithGenerateApi(replacements, retry.Token);
            }
            catch (TranslationResponseException ex)
            {
                if (attempt == _maxRetries)
                {
                    _logger.LogError(ex, "Too many requests. Max retries exhausted for text: {Text}", text);
                    throw new TranslationException("Too many requests. Retry limit reached.", ex);
                }

                await Task.Delay(delay, linked.Token).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(delay.Ticks * _retryDelayMultiplier);

                _logger.LogWarning(
                    "429 Too Many Requests. Retrying in {Delay}... (Attempt {Attempt}/{MaxRetries})",
                    delay, attempt, _maxRetries);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during translation attempt {Attempt}", attempt);
                throw new TranslationException("Unexpected error occurred during translation.", ex);
            }
        }

        throw new TranslationException("Translation failed after maximum retry attempts.");
    }

    /// <summary>
    /// Translates a batch of subtitles in a single API call using structured outputs fallback
    /// Since LocalAI may not support structured outputs, we'll attempt structured format first,
    /// then fall back to regular parsing if needed. Responses that cannot be parsed are retried
    /// using the configured retry settings, as local models occasionally emit malformed JSON.
    /// </summary>
    /// <param name="subtitleBatch">List of subtitles with position and content</param>
    /// <param name="sourceLanguage">Source language code</param>
    /// <param name="targetLanguage">Target language code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Dictionary mapping position to translated content</returns>
    public async Task<Dictionary<int, string>> TranslateBatchAsync(
        List<BatchSubtitleItem> subtitleBatch,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(sourceLanguage, targetLanguage);

        using var retry = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, retry.Token);
        
        var delay = _retryDelay;
        for (var attempt = 1; attempt <= _maxRetries; attempt++)
        {
            try
            {
                return await TranslateBatchWithLocalAiApi(subtitleBatch, linked.Token);
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                if (attempt == _maxRetries)
                {
                    _logger.LogError(ex, "Max retries exhausted ({StatusCode}) for batch translation", ex.StatusCode);
                    throw new TranslationException($"Retry limit reached after {ex.StatusCode}.", ex);
                }

                await Task.Delay(delay, linked.Token).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(delay.Ticks * _retryDelayMultiplier);

                _logger.LogWarning(
                    "{ServiceName} received {StatusCode}. Retrying in {Delay}... (Attempt {Attempt}/{MaxRetries})",
                    "LocalAI", ex.StatusCode, delay, attempt, _maxRetries);
            }
            catch (TranslationParseException ex)
            {
                if (attempt == _maxRetries)
                {
                    _logger.LogError(ex, "Max retries exhausted for batch translation, the model kept returning an unparsable response");
                    throw new TranslationException("Retry limit reached after unparsable response.", ex);
                }

                _logger.LogWarning(
                    "{ServiceName} returned an unparsable response. Retrying in {Delay}... (Attempt {Attempt}/{MaxRetries})",
                    "LocalAI", delay, attempt, _maxRetries);

                await Task.Delay(delay, linked.Token).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(delay.Ticks * _retryDelayMultiplier);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during batch translation attempt {Attempt}", attempt);
                throw new TranslationException("Unexpected error occurred during batch translation.", ex);
            }
        }

        throw new TranslationException("Batch translation failed after maximum retry attempts.");
    }

    private async Task<Dictionary<int, string>> TranslateBatchWithLocalAiApi(
        List<BatchSubtitleItem> subtitleBatch,
        CancellationToken cancellationToken)
    {
        if (!_isChatEndpoint)
        {
            return await TranslateBatchWithGenerateApi(subtitleBatch, cancellationToken);
        }

        // Try structured output first (OpenAI-compatible format)
        try
        {
            return await TranslateBatchWithStructuredOutput(subtitleBatch, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Structured output failed, falling back to JSON parsing");
            return await TranslateBatchWithJsonParsing(subtitleBatch, cancellationToken);
        }
    }

    private async Task<Dictionary<int, string>> TranslateBatchWithStructuredOutput(
        List<BatchSubtitleItem> subtitleBatch,
        CancellationToken cancellationToken)
    {
        var responseFormat = new
        {
            type = "json_schema",
            json_schema = new
            {
                name = "batch_translation_response",
                strict = true,
                schema = new
                {
                    type = "object",
                    properties = new
                    {
                        translations = new
                        {
                            type = "array",
                            items = new
                            {
                                type = "object",
                                properties = new
                                {
                                    position = new
                                    {
                                        type = "integer",
                                        description = "Position number of the subtitle item"
                                    },
                                    line = new
                                    {
                                        type = "string",
                                        description = "Translated subtitle text"
                                    }
                                },
                                required = new[] { "position", "line" },
                                additionalProperties = false
                            }
                        }
                    },
                    required = new[] { "translations" },
                    additionalProperties = false
                }
            }
        };

        var replacements = GetBatchReplacements(_model!, JsonSerializer.Serialize(subtitleBatch));
        var bodyJson = _requestTemplateService.BuildRequestBody(_chatRequestTemplate!, replacements);
        var fields = new Dictionary<string, object?>
        {
            ["response_format"] = responseFormat,
            ["stream"] = false
        };
        foreach (var opt in _modelOptions)
            fields[opt.Key] = opt.Value;
        bodyJson = _requestTemplateService.SetRequestFields(bodyJson, fields);

        var requestContent = new StringContent(
            bodyJson,
            Encoding.UTF8,
            "application/json");

        var response = await _httpClient.PostAsync(_endpoint, requestContent, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError(
                "LocalAI structured output batch request failed with status {StatusCode}: {ResponseContent}",
                response.StatusCode, 
                responseContent);
            throw new TranslationException(
                $"LocalAI structured output batch request failed with status {response.StatusCode}: {responseContent}");
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogDebug("Structured output raw response: {Response}", responseBody);

        // Try standard ChatResponse first, then direct JSON parsing if model returned raw translations
        ChatResponse? chatResponse = null;
        try
        {
            chatResponse = JsonSerializer.Deserialize<ChatResponse>(responseBody);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Structured output response is not a ChatResponse, attempting direct JSON parse");
        }

        string translatedJson;
        if (chatResponse?.Choices is { Count: > 0 })
        {
            translatedJson = chatResponse.Choices[0].Message.Content;
        }
        else
        {
            // Model returned raw JSON (e.g. {"translations": [...]}) — try parsing directly
            translatedJson = responseBody;
        }

        // Strip markdown fences if present
        translatedJson = translatedJson
            .Trim()
            .Replace("```json", "")
            .Replace("```", "");

        try
        {
            // Parse the wrapper object first, extract the translations array
            var responseWrapper = JsonSerializer.Deserialize<JsonElement>(translatedJson);
            if (!responseWrapper.TryGetProperty("translations", out var translationsElement))
            {
                throw new TranslationParseException("Response does not contain 'translations' property");
            }

            var translatedItems =
                JsonSerializer.Deserialize<List<StructuredBatchResponse>>(translationsElement.GetRawText());

            if (translatedItems == null)
            {
                throw new TranslationParseException("Failed to deserialize translated subtitles");
            }

            return MergeByPosition(translatedItems);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse structured JSON response: {Json}", translatedJson);
            throw new TranslationParseException("Failed to parse structured translated subtitles", ex);
        }
    }

    private async Task<Dictionary<int, string>> TranslateBatchWithJsonParsing(
        List<BatchSubtitleItem> subtitleBatch,
        CancellationToken cancellationToken)
    {
        var replacements = GetBatchReplacements(_model!, JsonSerializer.Serialize(subtitleBatch));
        // ponytail: enforce JSON output — structured output failed, model must return array
        replacements["systemPrompt"] +=
            "\n\nYou MUST respond with ONLY a JSON array. No prose, no explanation, no markdown. Example: [{\"position\": 1, \"line\": \"translated text\"}]";
        var bodyJson = _requestTemplateService.BuildRequestBody(_chatRequestTemplate!, replacements);
        var fields = new Dictionary<string, object?> { ["stream"] = false };
        foreach (var opt in _modelOptions)
            fields[opt.Key] = opt.Value;
        bodyJson = _requestTemplateService.SetRequestFields(bodyJson, fields);

        var requestContent = new StringContent(
            bodyJson,
            Encoding.UTF8,
            "application/json");

        var response = await _httpClient.PostAsync(_endpoint, requestContent, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError(
                "LocalAI JSON parsing batch request failed with status {StatusCode}: {ResponseContent}",
                response.StatusCode, 
                responseContent);
            throw new TranslationException(
                $"LocalAI JSON parsing batch request failed with status {response.StatusCode}: {responseContent}");
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        var chatResponse = JsonSerializer.Deserialize<ChatResponse>(responseBody);

        if (chatResponse?.Choices == null || chatResponse.Choices.Count == 0)
        {
            throw new TranslationException("No completion choices returned from LocalAI");
        }

        // Try to extract JSON — strip markdown fences, then locate array
        var translatedJson = chatResponse.Choices[0].Message.Content
            .Trim()
            .Replace("```json", "")
            .Replace("```", "");

        _logger.LogDebug("Raw model JSON-parsing response: {Response}", translatedJson);

        var jsonStart = translatedJson.IndexOf('[');
        var jsonEnd = translatedJson.LastIndexOf(']');
        if (jsonStart != -1 && jsonEnd != -1 && jsonEnd > jsonStart)
        {
            translatedJson = translatedJson.Substring(jsonStart, jsonEnd - jsonStart + 1);
        }

        translatedJson = translatedJson.Trim();
        if (string.IsNullOrEmpty(translatedJson) || translatedJson[0] != '[')
        {
            _logger.LogError(
                "Model did not return a JSON array. First 200 chars: {Preview}",
                translatedJson[..Math.Min(200, translatedJson.Length)]);
            throw new TranslationException(
                $"Model did not return a JSON array. Starts with: '{translatedJson[..Math.Min(80, translatedJson.Length)]}'");
        }

        try
        {
            var translatedItems = JsonSerializer.Deserialize<List<StructuredBatchResponse>>(translatedJson,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

            if (translatedItems == null)
            {
                throw new TranslationParseException("Failed to deserialize translated subtitles from JSON parsing");
            }

            return MergeByPosition(translatedItems);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse JSON response: {Json}", translatedJson);
            throw new TranslationParseException("Failed to parse JSON translated subtitles", ex);
        }
    }

    private async Task<Dictionary<int, string>> TranslateBatchWithGenerateApi(
        List<BatchSubtitleItem> subtitleBatch,
        CancellationToken cancellationToken)
    {
        var replacements = GetBatchReplacements(_model!, JsonSerializer.Serialize(subtitleBatch));
        replacements["systemPrompt"] +=
            "\n\nPlease return the response as a JSON array with objects containing 'position' and 'line' fields. Example: [{\"position\": 1, \"line\": \"translated text\"}]";
        var bodyJson = _requestTemplateService.BuildRequestBody(_generateRequestTemplate!, replacements);
        bodyJson = _requestTemplateService.SetRequestFields(bodyJson, new Dictionary<string, object?>
        {
            ["stream"] = false
        });

        var content = new StringContent(bodyJson,
            Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(_endpoint, content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError(
                "LocalAI generate API batch request failed with status {StatusCode}: {ResponseContent}",
                response.StatusCode, responseContent);
            throw new TranslationException(
                $"LocalAI generate API batch request failed with status {response.StatusCode}: {responseContent}");
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        var generateResponse = JsonSerializer.Deserialize<GenerateResponse>(responseBody);

        if (generateResponse == null || string.IsNullOrEmpty(generateResponse.Response))
        {
            throw new TranslationException("Invalid or empty response from generate API.");
        }

        var translatedJson = generateResponse.Response
            .Trim()
            .Replace("```json", "")
            .Replace("```", "");

        _logger.LogDebug("Raw generate API response: {Response}", translatedJson);

        // Try to extract JSON from the response
        var jsonStart = translatedJson.IndexOf('[');
        var jsonEnd = translatedJson.LastIndexOf(']');

        if (jsonStart != -1 && jsonEnd != -1 && jsonEnd > jsonStart)
        {
            translatedJson = translatedJson.Substring(jsonStart, jsonEnd - jsonStart + 1);
        }

        translatedJson = translatedJson.Trim();
        if (string.IsNullOrEmpty(translatedJson) || translatedJson[0] != '[')
        {
            _logger.LogError(
                "Generate API did not return a JSON array. First 200 chars: {Preview}",
                translatedJson[..Math.Min(200, translatedJson.Length)]);
            throw new TranslationException(
                $"Generate API did not return a JSON array. Starts with: '{translatedJson[..Math.Min(80, translatedJson.Length)]}'");
        }

        try
        {
            var translatedItems = JsonSerializer.Deserialize<List<StructuredBatchResponse>>(translatedJson);

            if (translatedItems == null)
            {
                throw new TranslationParseException("Failed to deserialize translated subtitles from generate API");
            }

            return MergeByPosition(translatedItems);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse generate API JSON response: {Json}", translatedJson);
            throw new TranslationParseException("Failed to parse generate API translated subtitles", ex);
        }
    }

    private async Task<string> TranslateWithGenerateApi(
        Dictionary<string, string> replacements,
        CancellationToken cancellationToken)
    {
        var bodyJson = _requestTemplateService.BuildRequestBody(_generateRequestTemplate!, replacements);


        var content = new StringContent(bodyJson,
            Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(_endpoint, content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError(
                "LocalAI generate API request failed with status {StatusCode}: {ResponseContent}",
                response.StatusCode, responseContent);
            throw new TranslationException(
                $"LocalAI generate API request failed with status {response.StatusCode}: {responseContent}");
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        var generateResponse = JsonSerializer.Deserialize<GenerateResponse>(responseBody);

        if (generateResponse == null || string.IsNullOrEmpty(generateResponse.Response))
        {
            throw new TranslationException("Invalid or empty response from generate API.");
        }

        return generateResponse.Response;
    }

    private async Task<string> TranslateWithChatApi(
        Dictionary<string, string> replacements,
        CancellationToken cancellationToken)
    {
        var bodyJson = _requestTemplateService.BuildRequestBody(_chatRequestTemplate!, replacements);
        var fields = new Dictionary<string, object?> { ["stream"] = false };
        foreach (var opt in _modelOptions)
            fields[opt.Key] = opt.Value;
        bodyJson = _requestTemplateService.SetRequestFields(bodyJson, fields);

        var content = new StringContent(bodyJson,
            Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(_endpoint, content, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError(
                "LocalAI chat API request to {Endpoint} failed with status {StatusCode}: {ResponseContent}",
                _endpoint, 
                response.StatusCode, 
                responseContent);
            throw new TranslationResponseException(
                $"LocalAI chat API request failed with status {response.StatusCode}: {responseContent}");
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        var chatResponse = JsonSerializer.Deserialize<ChatResponse>(responseBody);

        if (chatResponse?.Choices == null || chatResponse.Choices.Count == 0)
        {
            throw new TranslationResponseException("Invalid or empty response from chat API.");
        }

        return chatResponse.Choices[0].Message.Content;
    }

    /// <summary>
    /// Normalizes the endpoint URL: appends /chat/completions if only a base URL is provided.
    /// </summary>
    private static string NormalizeEndpoint(string endpoint)
    {
        var trimmed = endpoint.TrimEnd('/');

        // Already has a specific path
        if (trimmed.EndsWith("completions", StringComparison.OrdinalIgnoreCase) ||
            trimmed.EndsWith("generate", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        // Base URL like http://localhost:8080/v1 or http://localhost:8080
        return $"{trimmed}/chat/completions";
    }
}