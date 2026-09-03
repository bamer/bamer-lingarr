using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lingarr.Contracts.Exceptions;
using Lingarr.Contracts.Models.Batch;
using Lingarr.Contracts.Translation;
using Lingarr.Core.Configuration;
using Lingarr.Server.Exceptions;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Models.Integrations.Translation;
using Lingarr.Server.Services.Translation.Base;

namespace Lingarr.Server.Services.Translation;

public class LocalAiService : BaseLanguageService, ITranslationService, IBatchTranslationService, IProofreadService
{
    private readonly HttpClient _httpClient;
    private readonly IRequestTemplateService _requestTemplateService;
    private bool _httpClientConfigured;
    private string? _model;
    private string? _endpoint;
    private string? _chatRequestTemplate;
    private string? _generateRequestTemplate;
    private bool _isChatEndpoint;
    private bool _useStructuredOutput;
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
    /// Reads fresh settings on every call. HttpClient timeout/auth must be set once
    /// before first request — handled separately in SetupHttpClientIfNeeded.
    /// </summary>
    private async Task InitializeAsync(string sourceLanguage, string targetLanguage)
    {
        // Always read fresh settings — each request gets current config
        var settings = await _settings.GetSettings([
            SettingKeys.Translation.LocalAi.Model,
            SettingKeys.Translation.LocalAi.Endpoint,
            SettingKeys.Translation.LocalAi.ChatRequestTemplate,
            SettingKeys.Translation.LocalAi.GenerateRequestTemplate,
            SettingKeys.Translation.AiPrompt,
            SettingKeys.Translation.AiUserPrompt,
            SettingKeys.Translation.ProofreadPrompt,
            SettingKeys.Translation.ProofreadUserPrompt,
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
            SettingKeys.Translation.ModelReasoningEffort,
            SettingKeys.Translation.ModelStructuredOutput
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

            _proofreadPrompt = settings.GetValueOrDefault(SettingKeys.Translation.ProofreadPrompt);
            _proofreadUserPrompt = settings.GetValueOrDefault(SettingKeys.Translation.ProofreadUserPrompt);

            // Normalize endpoint URLs — append path if only base URL is provided
            _endpoint = NormalizeEndpoint(_endpoint);
            _isChatEndpoint = _endpoint.TrimEnd('/').EndsWith("completions", StringComparison.OrdinalIgnoreCase);
            _useStructuredOutput = settings.TryGetValue(SettingKeys.Translation.ModelStructuredOutput, out var soStr)
                && soStr == "true";

            // HttpClient timeout/headers can only be set before first request
            if (!_httpClientConfigured)
            {
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
                    _httpClient.DefaultRequestHeaders.Authorization =
                        new AuthenticationHeaderValue("Bearer", apiKey);
                }

                _httpClientConfigured = true;
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
                return await CompleteWithLocalAiApi(replacements, linked.Token);
            }
            catch (TranslationResponseException ex)
            {
                if (attempt == _maxRetries)
                {
                    _logger.LogError(ex, "Too many requests. Max retries exhausted for text: {Text}", text);
                    throw new TranslationException("Too many requests. Retry limit reached.", ex);
                }

et                 await Task.Delay(delay, linked.Token).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(delay.Ticks * _retryDelayMultiplier);

                _logger.LogWarning(
                    "429 Too Many Requests. Retrying in {Delay}... (Attempt {Attempt}/{MaxRetries})",
                    delay, attempt, _maxRetries);
            }
            catch (HttpRequestException ex) when (IsTransientFailure(ex.StatusCode))
            {
                if (attempt == _maxRetries)
                {
                    _logger.LogError(ex, "Max retries exhausted ({StatusCode}) for text: {Text}", ex.StatusCode, text);
                    throw new TranslationException(
                        $"Retry limit reached after {ex.StatusCode?.ToString() ?? "a transport error"}.", ex);
                }

                await Task.Delay(delay, linked.Token).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(delay.Ticks * _retryDelayMultiplier);

                _logger.LogWarning(
                    "{ServiceName} received {StatusCode}. Retrying in {Delay}... (Attempt {Attempt}/{MaxRetries})",
                    "LocalAI", ex.StatusCode, delay, attempt, _maxRetries);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                // The request exceeded the configured timeout while the backend was busy — retryable
                if (attempt == _maxRetries)
                {
                    _logger.LogError(ex, "Max retries exhausted after request timeout for text: {Text}", text);
                    throw new TranslationException("Retry limit reached after request timeout.", ex);
                }

                await Task.Delay(delay, linked.Token).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(delay.Ticks * _retryDelayMultiplier);

                _logger.LogWarning(
                    "{ServiceName} request timed out. Retrying in {Delay}... (Attempt {Attempt}/{MaxRetries})",
                    "LocalAI", delay, attempt, _maxRetries);
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

    /// <inheritdoc />
    public async Task<string> ProofreadAsync(
        string sourceText,
        string translatedText,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(sourceLanguage, targetLanguage);

        var replacements = GetProofreadReplacements(_model!, sourceText, translatedText);
        using var retry = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, retry.Token);

        var delay = _retryDelay;
        for (var attempt = 1; attempt <= _maxRetries; attempt++)
        {
            try
            {
                return await CompleteWithLocalAiApi(replacements, linked.Token);
            }
            catch (TranslationResponseException ex)
            {
                if (attempt == _maxRetries)
                {
                    _logger.LogError(ex, "Too many requests. Max retries exhausted for text: {Text}", translatedText);
                    throw new TranslationException("Too many requests. Retry limit reached.", ex);
                }

                await Task.Delay(delay, linked.Token).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(delay.Ticks * _retryDelayMultiplier);

                _logger.LogWarning(
                    "429 Too Many Requests. Retrying in {Delay}... (Attempt {Attempt}/{MaxRetries})",
                    delay, attempt, _maxRetries);
            }
            catch (HttpRequestException ex) when (IsTransientFailure(ex.StatusCode))
            {
                if (attempt == _maxRetries)
                {
                    _logger.LogError(ex, "Max retries exhausted ({StatusCode}) during proofread", ex.StatusCode);
                    throw new TranslationException(
                        $"Retry limit reached after {ex.StatusCode?.ToString() ?? "a transport error"}.", ex);
                }

                await Task.Delay(delay, linked.Token).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(delay.Ticks * _retryDelayMultiplier);

                _logger.LogWarning(
                    "{ServiceName} received {StatusCode}. Retrying in {Delay}... (Attempt {Attempt}/{MaxRetries})",
                    "LocalAI", ex.StatusCode, delay, attempt, _maxRetries);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                // The request exceeded the configured timeout while the backend was busy — retryable
                if (attempt == _maxRetries)
                {
                    _logger.LogError(ex, "Max retries exhausted after request timeout during proofread");
                    throw new TranslationException("Retry limit reached after request timeout.", ex);
                }

                await Task.Delay(delay, linked.Token).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(delay.Ticks * _retryDelayMultiplier);

                _logger.LogWarning(
                    "{ServiceName} request timed out. Retrying in {Delay}... (Attempt {Attempt}/{MaxRetries})",
                    "LocalAI", delay, attempt, _maxRetries);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during proofread attempt {Attempt}", attempt);
                throw new TranslationException("Unexpected error occurred during proofread.", ex);
            }
        }

        throw new TranslationException("Proofread failed after maximum retry attempts.");
    }

    private async Task<string> CompleteWithLocalAiApi(
        Dictionary<string, string> replacements,
        CancellationToken cancellationToken)
    {
        return _isChatEndpoint
            ? await TranslateWithChatApi(replacements, cancellationToken)
            : await TranslateWithGenerateApi(replacements, cancellationToken);
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
            catch (HttpRequestException ex) when (IsTransientFailure(ex.StatusCode))
            {
                if (attempt == _maxRetries)
                {
                    _logger.LogError(ex, "Max retries exhausted ({StatusCode}) for batch translation", ex.StatusCode);
                    throw new TranslationException(
                        $"Retry limit reached after {ex.StatusCode?.ToString() ?? "a transport error"}.", ex);
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
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                // The request exceeded the configured timeout while the backend was busy — retryable
                if (attempt == _maxRetries)
                {
                    _logger.LogError(ex, "Max retries exhausted after request timeout for batch translation");
                    throw new TranslationException("Retry limit reached after request timeout.", ex);
                }

                await Task.Delay(delay, linked.Token).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(delay.Ticks * _retryDelayMultiplier);

                _logger.LogWarning(
                    "{ServiceName} request timed out. Retrying in {Delay}... (Attempt {Attempt}/{MaxRetries})",
                    "LocalAI", delay, attempt, _maxRetries);
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

        // Try structured output only if enabled
        if (_useStructuredOutput)
        {
            try
            {
                return await TranslateBatchWithStructuredOutput(subtitleBatch, cancellationToken);
            }
            catch (HttpRequestException ex) when (IsTransientFailure(ex.StatusCode))
            {
                // The backend is temporarily unavailable/overloaded — bubble up so the retry
                // loop can back off instead of immediately hammering it with a fallback request.
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Structured output failed, falling back to JSON parsing");
            }
        }

        return await TranslateBatchWithJsonParsing(subtitleBatch, cancellationToken);
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
            await ThrowOnUnsuccessfulResponse(response, "structured output batch", cancellationToken);
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
            await ThrowOnUnsuccessfulResponse(response, "JSON parsing batch", cancellationToken);
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        var chatResponse = JsonSerializer.Deserialize<ChatResponse>(responseBody);

        if (chatResponse?.Choices == null || chatResponse.Choices.Count == 0)
        {
            // Model glitch, not a configuration problem — the retry loop treats this as retryable
            throw new TranslationParseException("No completion choices returned from LocalAI");
        }

        // Try to extract JSON — strip markdown fences, then locate array
        var translatedJson = chatResponse.Choices[0].Message.Content
            .Trim()
            .Replace("```json", "")
            .Replace("```", "");

        _logger.LogDebug("Raw model JSON-parsing response: {Response}", translatedJson);

        translatedJson = RepairTruncatedJson(translatedJson);

        // Step 1: Try parsing as-is
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
        catch (JsonException) { }

        // Step 2: Try repairing stray characters and parsing again
        var repaired = RepairBrokenJson(translatedJson);
        try
        {
            var translatedItems = JsonSerializer.Deserialize<List<StructuredBatchResponse>>(repaired,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

            if (translatedItems != null)
            {
                _logger.LogWarning("JSON repaired successfully after cleaning stray characters");
                return MergeByPosition(translatedItems);
            }
        }
        catch (JsonException) { }

        // Step 3: Last resort — regex fallback for truly broken JSON
        var fallbackItems = ExtractTranslatedLines(translatedJson);
        if (fallbackItems.Count > 0)
        {
            _logger.LogWarning("JSON parse failed, recovered {Count} lines via regex fallback", fallbackItems.Count);
            return MergeByPosition(fallbackItems);
        }

        _logger.LogError("Failed to parse JSON response: {Json}", translatedJson);
        throw new TranslationParseException("Failed to parse JSON translated subtitles");
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
            await ThrowOnUnsuccessfulResponse(response, "generate API batch", cancellationToken);
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
            await ThrowOnUnsuccessfulResponse(response, "generate API", cancellationToken);
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
            await ThrowOnUnsuccessfulResponse(response, "chat API", cancellationToken);
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
    /// Transient backend conditions worth retrying: rate limiting (429), server-side
    /// errors (5xx) and transport-level failures without a status code such as a
    /// connection refused/reset while the local backend is starting up or restarting.
    /// </summary>
    private static bool IsTransientFailure(HttpStatusCode? statusCode) =>
        statusCode is null
        || statusCode == HttpStatusCode.TooManyRequests
        || (int)statusCode >= 500;

    /// <summary>
    /// Logs and throws an HttpRequestException that carries the response status code, so
    /// the retry loop can distinguish transient backend errors (429/5xx) from permanent
    /// failures (400/401/...), which should fail fast without retrying.
    /// </summary>
    private async Task ThrowOnUnsuccessfulResponse(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogError(
            "LocalAI {Operation} request to {Endpoint} failed with status {StatusCode}: {ResponseContent}",
            operation, _endpoint, response.StatusCode, responseContent);
        throw new HttpRequestException(
            $"LocalAI {operation} request failed with status {response.StatusCode}: {responseContent}",
            inner: null,
            statusCode: response.StatusCode);
    }

    /// <summary>
    /// Attempts to repair broken JSON by cleaning stray characters injected by models.
    /// Only used when standard parse fails — not applied to valid JSON.
    /// </summary>
    private static string RepairBrokenJson(string json)
    {
        // Strip stray ) after closing quote: "text") → "text"
        // Only match ") followed by , or } or ] (not valid JSON like "},")
        var result = Regex.Replace(json, @"""\s*\)\s*([},\]])", @"""$1");
        return result;
    }

    /// <summary>
    /// Regex fallback that extracts position/line pairs from broken JSON.
    /// Handles: missing position numbers, stray characters, truncated output.
    /// </summary>
    private static List<StructuredBatchResponse> ExtractTranslatedLines(string json)
    {
        var results = new List<StructuredBatchResponse>();

        // Match {"position":<optional_number>,"line":"<value>"} or variations
        var pattern = @"\{""position"":\s*(\d+)?\s*,\s*""line""\s*:\s*""((?:[^""\\]|\\.)*)""\s*\}";
        var matches = Regex.Matches(json, pattern);

        // If we got matches with position numbers, use them
        foreach (Match match in matches)
        {
            if (match.Groups[1].Success && int.TryParse(match.Groups[1].Value, out var position))
            {
                var line = match.Groups[2].Value
                    .Replace("\\\"", "\"")
                    .Replace("\\n", "\n");
                results.Add(new StructuredBatchResponse { Position = position, Line = line });
            }
        }

        if (results.Count > 0)
        {
            return results;
        }

        // If no position numbers found, extract lines and assign sequential positions
        var linePattern = @"""line""\s*:\s*""((?:[^""\\]|\\.)*)""";
        var lineMatches = Regex.Matches(json, linePattern);
        var assumedPosition = 1;
        foreach (Match match in lineMatches)
        {
            var line = match.Groups[1].Value
                .Replace("\\\"", "\"")
                .Replace("\\n", "\n");
            results.Add(new StructuredBatchResponse { Position = assumedPosition++, Line = line });
        }

        return results;
    }

    /// <summary>
    /// Attempts to repair truncated/malformed JSON from model output.
    /// Handles: missing opening [, extra ], truncated arrays, missing closing ].
    /// </summary>
    private string RepairTruncatedJson(string json)
    {
        json = json.Trim();

        // Extract between first [ and last ] if present
        var jsonStart = json.IndexOf('[');
        var jsonEnd = json.LastIndexOf(']');
        if (jsonStart != -1 && jsonEnd != -1 && jsonEnd > jsonStart)
        {
            json = json.Substring(jsonStart, jsonEnd - jsonStart + 1);
        }

        // Try parse as-is first
        try
        {
            JsonSerializer.Deserialize<List<StructuredBatchResponse>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return json;
        }
        catch (JsonException) { }

        // If starts with [ but fails, it might be truncated — find last complete object
        if (json.StartsWith('['))
        {
            var lastBrace = json.LastIndexOf('}');
            if (lastBrace != -1 && lastBrace < json.Length - 1)
            {
                // Has content after last }, try truncating there + closing array
                var candidate = json.Substring(0, lastBrace + 1) + "]";
                try
                {
                    JsonSerializer.Deserialize<List<StructuredBatchResponse>>(candidate,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    return candidate;
                }
                catch (JsonException) { }
            }
            else if (lastBrace != -1)
            {
                // Ends with }, missing ]
                var candidate = json + "]";
                try
                {
                    JsonSerializer.Deserialize<List<StructuredBatchResponse>>(candidate,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    return candidate;
                }
                catch (JsonException) { }
            }
        }

        // No [ found — find first { and wrap in array
        var firstBrace = json.IndexOf('{');
        if (firstBrace != -1)
        {
            var candidate = "[" + json.Substring(firstBrace);
            if (!candidate.EndsWith(']')) candidate += "]";
            try
            {
                JsonSerializer.Deserialize<List<StructuredBatchResponse>>(candidate,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return candidate;
            }
            catch (JsonException) { }

            // Also try truncating to last }
            var lastBraceInCandidate = candidate.LastIndexOf('}');
            if (lastBraceInCandidate != -1)
            {
                candidate = candidate.Substring(0, lastBraceInCandidate + 1) + "]";
                try
                {
                    JsonSerializer.Deserialize<List<StructuredBatchResponse>>(candidate,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    return candidate;
                }
                catch (JsonException) { }
            }
        }

        // If we get here, repair failed — return original and let caller handle the error
        _logger.LogWarning("JSON repair attempts failed for model output");
        return json;
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