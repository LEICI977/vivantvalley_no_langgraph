using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VivantValley.Services;

/// <summary>
/// Sends non-streaming, provider-native tool calls directly from the SMAPI
/// process. It never starts a child process or opens a local listener.
/// </summary>
public sealed class ConversationToolProviderClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient httpClient;

    public ConversationToolProviderClient(HttpClient httpClient)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<ConversationProviderResponse> CompleteAsync(
        AiRuntimeProfile profile,
        IReadOnlyList<ConversationProviderMessage> messages,
        IReadOnlyList<JsonElement> tools,
        object? toolChoice,
        int maxOutputTokens,
        CancellationToken cancellationToken = default,
        string? idempotencyKey = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(tools);
        if (string.IsNullOrWhiteSpace(profile.ApiKey))
            throw new DeepSeekConfigurationException($"{profile.Provider} API Key 为空，请先打开 AI 设置。");
        if (string.IsNullOrWhiteSpace(profile.Model))
            throw new DeepSeekConfigurationException($"{profile.Provider} 模型不能为空。");
        if (messages.Count == 0)
            throw new DeepSeekConfigurationException("工具对话消息不能为空。");

        int tokenLimit = Math.Clamp(maxOutputTokens, 128, 2048);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = profile.Model,
            ["messages"] = messages,
            ["stream"] = false,
            ["temperature"] = 0.75d,
            ["top_p"] = 0.9d,
        };
        if (tools.Count > 0)
        {
            payload["tools"] = tools;
            payload["tool_choice"] = toolChoice ?? "auto";
        }

        if (AiProviderNames.Normalize(profile.Provider) == AiProviderNames.Hosted)
        {
            payload["max_tokens"] = tokenLimit;
        }
        else if (AiProviderNames.Normalize(profile.Provider) == AiProviderNames.OpenAI)
        {
            payload["max_completion_tokens"] = tokenLimit;
        }
        else
        {
            payload["max_tokens"] = tokenLimit;
            payload["thinking"] = new { type = profile.EnableThinking ? "enabled" : "disabled" };
            if (profile.EnableThinking)
                payload["reasoning_effort"] = profile.ReasoningEffort;
        }

        string json = JsonSerializer.Serialize(payload, JsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, profile.Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile.ApiKey);
        if (AiProviderNames.Normalize(profile.Provider) == AiProviderNames.Hosted)
        {
            string key = string.IsNullOrWhiteSpace(idempotencyKey)
                ? Guid.NewGuid().ToString("N")
                : idempotencyKey.Trim();
            if (key.Length > 128)
                throw new DeepSeekConfigurationException("托管请求幂等键无效。");
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        }
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (profile.RequestTimeout != Timeout.InfiniteTimeSpan)
            timeout.CancelAfter(profile.RequestTimeout);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DeepSeekTimeoutException(
                $"{profile.Provider} 工具请求在 {profile.RequestTimeout.TotalSeconds:0.#} 秒后超时。",
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw new DeepSeekApiException(
                $"无法连接 {profile.Provider} API：{Sanitize(exception.Message, profile.ApiKey)}",
                statusCode: null,
                exception);
        }

        using (response)
        {
            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new DeepSeekTimeoutException(
                    $"读取 {profile.Provider} 工具响应在 {profile.RequestTimeout.TotalSeconds:0.#} 秒后超时。",
                    exception);
            }
            catch (Exception exception) when (exception is HttpRequestException or ObjectDisposedException)
            {
                throw new DeepSeekApiException(
                    $"读取 {profile.Provider} 工具响应失败：{Sanitize(exception.Message, profile.ApiKey)}",
                    response.StatusCode,
                    exception);
            }

            if (!response.IsSuccessStatusCode)
            {
                string providerMessage = ExtractProviderError(body, profile.ApiKey);
                string prefix = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                        => $"{profile.Provider} API 鉴权失败，请检查 API Key。",
                    (HttpStatusCode)429
                        => $"{profile.Provider} API 请求过于频繁或额度不足（HTTP 429）。",
                    _ => $"{profile.Provider} API 请求失败（HTTP {(int)response.StatusCode} {response.StatusCode}）。",
                };
                throw new DeepSeekApiException(
                    providerMessage.Length == 0 ? prefix : prefix + " 服务端信息：" + providerMessage,
                    response.StatusCode);
            }

            return ParseResponse(body, response.StatusCode, profile);
        }
    }

    private static ConversationProviderResponse ParseResponse(
        string json,
        HttpStatusCode statusCode,
        AiRuntimeProfile profile)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("choices", out JsonElement choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0
                || !choices[0].TryGetProperty("message", out JsonElement message)
                || message.ValueKind != JsonValueKind.Object)
            {
                throw new DeepSeekApiException(
                    $"{profile.Provider} 响应缺少 choices[0].message。",
                    statusCode);
            }

            string content = message.TryGetProperty("content", out JsonElement contentElement)
                             && contentElement.ValueKind == JsonValueKind.String
                ? contentElement.GetString() ?? string.Empty
                : string.Empty;
            var toolCalls = new List<ConversationProviderToolCall>();
            if (message.TryGetProperty("tool_calls", out JsonElement callsElement)
                && callsElement.ValueKind != JsonValueKind.Null)
            {
                if (callsElement.ValueKind != JsonValueKind.Array)
                    throw new JsonException("message.tool_calls must be an array");
                foreach (JsonElement call in callsElement.EnumerateArray())
                {
                    if (call.ValueKind != JsonValueKind.Object
                        || !call.TryGetProperty("function", out JsonElement function)
                        || function.ValueKind != JsonValueKind.Object)
                    {
                        throw new JsonException("tool call is missing its function object");
                    }

                    string id = GetString(call, "id");
                    string name = GetString(function, "name");
                    string rawArguments = function.TryGetProperty("arguments", out JsonElement arguments)
                        ? arguments.ValueKind == JsonValueKind.String
                            ? arguments.GetString() ?? string.Empty
                            : arguments.GetRawText()
                        : string.Empty;
                    JsonElement parsedArguments;
                    try
                    {
                        using JsonDocument argumentsDocument = JsonDocument.Parse(rawArguments);
                        parsedArguments = argumentsDocument.RootElement.Clone();
                    }
                    catch (JsonException exception)
                    {
                        throw new JsonException("tool call arguments are not valid JSON", exception);
                    }

                    toolCalls.Add(new ConversationProviderToolCall(
                        id,
                        name,
                        rawArguments,
                        parsedArguments));
                }
            }

            return new ConversationProviderResponse(content.Trim(), toolCalls);
        }
        catch (DeepSeekApiException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new DeepSeekApiException(
                $"{profile.Provider} 返回了无法解析的工具响应：{Sanitize(exception.Message, profile.ApiKey)}",
                statusCode,
                exception);
        }
    }

    private static string GetString(JsonElement value, string propertyName)
        => value.TryGetProperty(propertyName, out JsonElement property)
           && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private static string ExtractProviderError(string responseBody, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
            return string.Empty;
        try
        {
            using JsonDocument document = JsonDocument.Parse(responseBody);
            if (document.RootElement.TryGetProperty("error", out JsonElement error))
            {
                if (error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("message", out JsonElement message)
                    && message.ValueKind == JsonValueKind.String)
                {
                    return Sanitize(message.GetString() ?? string.Empty, apiKey);
                }
                return Sanitize(error.ToString(), apiKey);
            }
        }
        catch (JsonException)
        {
        }
        return Sanitize(responseBody, apiKey);
    }

    private static string Sanitize(string value, string apiKey)
    {
        string clean = value ?? string.Empty;
        if (!string.IsNullOrEmpty(apiKey))
            clean = clean.Replace(apiKey, "[REDACTED]", StringComparison.Ordinal);
        clean = clean.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return clean.Length <= 500 ? clean : clean[..500] + "…";
    }
}

public sealed class ConversationProviderMessage
{
    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    [JsonPropertyName("content")]
    public string? Content { get; init; }

    [JsonPropertyName("tool_calls")]
    public IReadOnlyList<ConversationProviderRequestToolCall>? ToolCalls { get; init; }

    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    public static ConversationProviderMessage System(string content)
        => new() { Role = "system", Content = content };

    public static ConversationProviderMessage User(string content)
        => new() { Role = "user", Content = content };

    public static ConversationProviderMessage Assistant(string content)
        => new() { Role = "assistant", Content = content };

    public static ConversationProviderMessage AssistantToolCall(ConversationProviderToolCall call)
        => new()
        {
            Role = "assistant",
            Content = string.Empty,
            ToolCalls = new[]
            {
                new ConversationProviderRequestToolCall
                {
                    Id = call.Id,
                    Function = new ConversationProviderRequestFunction
                    {
                        Name = call.Name,
                        Arguments = call.RawArguments,
                    },
                },
            },
        };

    public static ConversationProviderMessage Tool(
        string toolCallId,
        string content)
        => new()
        {
            Role = "tool",
            Content = content,
            ToolCallId = toolCallId,
        };
}

public sealed class ConversationProviderRequestToolCall
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; init; } = "function";

    [JsonPropertyName("function")]
    public ConversationProviderRequestFunction Function { get; init; } = new();
}

public sealed class ConversationProviderRequestFunction
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("arguments")]
    public string Arguments { get; init; } = "{}";
}

public sealed record ConversationProviderToolCall(
    string Id,
    string Name,
    string RawArguments,
    JsonElement Arguments);

public sealed record ConversationProviderResponse(
    string Content,
    IReadOnlyList<ConversationProviderToolCall> ToolCalls);
