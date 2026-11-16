using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using InterviewCopilot.Application.Providers;
using Microsoft.Extensions.Logging;

namespace InterviewCopilot.Infrastructure.Llm;

/// <summary>Google Gemini <c>streamGenerateContent</c> over SSE (BYOK, opt-in; #30).</summary>
internal sealed class GoogleLlmProvider(HttpClient http, ILogger<GoogleLlmProvider>? logger = null) : CloudLlmProviderBase(http, logger)
{
    internal const string KeyHeader = "x-goog-api-key";

    public override string Name => ProviderNames.Google;

    protected override string DisplayName => "Google Gemini";

    protected override string ErrorMessage(HttpResponseMessage response) =>
        CloudProviderErrors.FromGoogleStatus(response);

    protected override HttpRequestMessage BuildMessage(LlmRequest request, string apiKey)
    {
        var generationConfig = new JsonObject();
        if (request.Model.Equals("gemini-3.5-flash-lite", StringComparison.OrdinalIgnoreCase))
        {
            generationConfig["thinkingConfig"] = new JsonObject { ["thinkingLevel"] = "MINIMAL" };
        }
        else if (request.Model.Equals("gemini-3.8-flash", StringComparison.OrdinalIgnoreCase))
        {
            generationConfig["thinkingConfig"] = new JsonObject { ["thinkingLevel"] = "LOW" };
        }

        if (!request.Model.StartsWith("gemini-3.", StringComparison.OrdinalIgnoreCase))
        {
            generationConfig["temperature"] = request.Temperature;
        }

        var body = new JsonObject
        {
            ["contents"] = new JsonArray([.. request.Messages
                .Where(m => m.Role != LlmRole.System)
                .Select(m => (JsonNode)new JsonObject
                {
                    ["role"] = m.Role == LlmRole.Assistant ? "model" : "user",
                    ["parts"] = new JsonArray(new JsonObject { ["text"] = m.Content }),
                })]),
            ["generationConfig"] = generationConfig,
        };

        var system = string.Join("\n\n", request.Messages.Where(m => m.Role == LlmRole.System).Select(m => m.Content));
        if (system.Length > 0)
        {
            body["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = system }) };
        }

        var path = $"v1beta/models/{Uri.EscapeDataString(request.Model)}:streamGenerateContent?alt=sse";
        var message = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        message.Headers.Add(KeyHeader, apiKey);
        return message;
    }

    protected override CloudChunk Parse(string data)
    {
        using var document = JsonDocument.Parse(data);
        var root = document.RootElement;
        if (root.TryGetProperty("error", out var error))
        {
            int? statusCode = error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.Number
                && code.TryGetInt32(out var parsedCode)
                    ? parsedCode
                    : null;
            throw new LlmUnavailableException(CloudProviderErrors.FromGoogleStreamError(statusCode));
        }

        if (!root.TryGetProperty("candidates", out var candidates)
            || candidates.ValueKind != JsonValueKind.Array
            || candidates.GetArrayLength() == 0
            || !candidates[0].TryGetProperty("content", out var content)
            || !content.TryGetProperty("parts", out var parts)
            || parts.ValueKind != JsonValueKind.Array)
        {
            return default;
        }

        var text = string.Concat(parts.EnumerateArray()
            .Select(p => p.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null));
        return new CloudChunk(text, false);
    }
}

/// <summary>Gemini <c>GET v1beta/models</c> with the user's key, keeping models that can generate content.</summary>
internal sealed class GoogleModelCatalog(HttpClient http) : CloudModelCatalogBase(http)
{
    private const string Prefix = "models/";

    public override string Provider => ProviderNames.Google;

    protected override string DisplayName => "Google Gemini";

    protected override Exception ErrorForResponse(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.Unauthorized
            ? new LlmKeyRejectedException(CloudProviderErrors.Rejected(DisplayName))
            : new LlmUnavailableException(CloudProviderErrors.FromGoogleStatus(response));

    protected override HttpRequestMessage BuildMessage(string apiKey)
    {
        var message = new HttpRequestMessage(HttpMethod.Get, "v1beta/models?pageSize=1000");
        message.Headers.Add(GoogleLlmProvider.KeyHeader, apiKey);
        return message;
    }

    protected override IEnumerable<CloudModelInfo> Parse(JsonElement root) =>
        Items(root, "models")
            .Where(CanGenerate)
            .Select(item => (Name: Text(item, "name"), Display: Text(item, "displayName")))
            .Where(m => !string.IsNullOrWhiteSpace(m.Name))
            .Select(m =>
            {
                var id = m.Name!.StartsWith(Prefix, StringComparison.Ordinal) ? m.Name[Prefix.Length..] : m.Name;
                return new CloudModelInfo(id, string.IsNullOrWhiteSpace(m.Display) ? id : m.Display);
            });

    private static bool CanGenerate(JsonElement item) =>
        item.ValueKind == JsonValueKind.Object
        && item.TryGetProperty("supportedGenerationMethods", out var methods)
        && methods.ValueKind == JsonValueKind.Array
        && methods.EnumerateArray().Any(m => m.ValueKind == JsonValueKind.String && m.GetString() == "generateContent");
}
