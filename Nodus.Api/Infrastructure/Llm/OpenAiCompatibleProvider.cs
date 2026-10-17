using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nodus.Api.Infrastructure.Llm;

/// <summary>Single attempt against one OpenAI-compatible endpoint (neuroapi.host, Timeweb Cloud AI gateway).</summary>
public sealed class OpenAiCompatibleProvider(HttpClient http, LlmProviderOptions options)
{
    public LlmProviderOptions Options => options;

    public async Task<(string Content, int? PromptTokens, int? CompletionTokens)> SendAsync(LlmRequest request, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"{options.BaseUrl.TrimEnd('/')}/v1/chat/completions")
        {
            Content = new StringContent(BuildBody(request).ToJsonString(), Encoding.UTF8, "application/json")
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        HttpResponseMessage response;
        string body;
        try
        {
            response = await http.SendAsync(message, timeout.Token);
            body = await response.Content.ReadAsStringAsync(timeout.Token);
        }
        catch (OperationCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new LlmException(LlmErrorCategory.Timeout, options.Name, "LLM request timed out.", e);
        }
        catch (HttpRequestException e)
        {
            throw new LlmException(LlmErrorCategory.Network, options.Name, "LLM provider is unreachable.", e);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new LlmException(Categorize(response.StatusCode), options.Name,
                    $"LLM provider returned {(int)response.StatusCode}.");
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var content = root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            if (string.IsNullOrWhiteSpace(content))
                throw new LlmException(LlmErrorCategory.InvalidResponse, options.Name, "LLM returned empty content.");
            int? Usage(string name) =>
                root.TryGetProperty("usage", out var usage) && usage.TryGetProperty(name, out var value) &&
                value.TryGetInt32(out var tokens) ? tokens : null;
            return (StripCodeFence(content), Usage("prompt_tokens"), Usage("completion_tokens"));
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            throw new LlmException(LlmErrorCategory.InvalidResponse, options.Name, "LLM response is not a chat completion.", e);
        }
    }

    private JsonObject BuildBody(LlmRequest request)
    {
        var body = new JsonObject
        {
            ["model"] = options.Model,
            ["temperature"] = request.Temperature,
            ["messages"] = new JsonArray(request.Messages
                .Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content }).ToArray())
        };
        if (request.MaxTokens is { } maxTokens) body["max_tokens"] = maxTokens;
        if (request.Schema is { } schema)
            body["response_format"] = options.SupportsJsonSchema
                ? new JsonObject
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new JsonObject
                    {
                        ["name"] = schema.Name, ["strict"] = true, ["schema"] = JsonNode.Parse(schema.Schema.GetRawText())
                    }
                }
                : new JsonObject { ["type"] = "json_object" };
        return body;
    }

    private static LlmErrorCategory Categorize(HttpStatusCode status) => status switch
    {
        HttpStatusCode.TooManyRequests => LlmErrorCategory.RateLimited,
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.PaymentRequired => LlmErrorCategory.Unauthorized,
        HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => LlmErrorCategory.Timeout,
        >= HttpStatusCode.InternalServerError => LlmErrorCategory.ProviderError,
        _ => LlmErrorCategory.BadRequest
    };

    /// <summary>Some models wrap JSON in a markdown fence even in JSON mode.</summary>
    private static string StripCodeFence(string content)
    {
        var trimmed = content.Trim();
        if (!trimmed.StartsWith("```")) return trimmed;
        var start = trimmed.IndexOf('\n');
        var end = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        return start > 0 && end > start ? trimmed[(start + 1)..end].Trim() : trimmed;
    }
}
