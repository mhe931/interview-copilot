using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using InterviewCopilot.Application.Providers;
using Microsoft.Extensions.Logging;

namespace InterviewCopilot.Infrastructure.Llm;

/// <summary>Local, free LLM via Ollama's streaming chat API (default provider, NFR-01).</summary>
internal sealed partial class OllamaLlmProvider(HttpClient http, ILogger<OllamaLlmProvider>? logger = null) : ILlmProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private ILogger<OllamaLlmProvider>? Logger { get; } = logger;

    public string Name => ProviderNames.Ollama;

    public IAsyncEnumerable<string> StreamAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return StreamCoreAsync(request, cancellationToken);
    }

    private async IAsyncEnumerable<string> StreamCoreAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var body = new OllamaChatRequest(
            request.Model,
            [.. request.Messages.Select(m => new OllamaMessage(RoleName(m.Role), m.Content))],
            Stream: true,
            new OllamaRequestOptions(request.Temperature));

        using var message = new HttpRequestMessage(HttpMethod.Post, "api/chat")
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };

        var requestStartedAt = Stopwatch.GetTimestamp();
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            if (Logger is not null)
            {
                LogRequestFailed(
                    Logger,
                    request.Model,
                    request.RequestId,
                    null,
                    Stopwatch.GetElapsedTime(requestStartedAt).TotalMilliseconds);
            }

            throw new LlmUnavailableException(
                "Can't reach Ollama. Make sure it's installed and running (https://ollama.com).", ex);
        }

        using (response)
        {
            var headersElapsedMs = Stopwatch.GetElapsedTime(requestStartedAt).TotalMilliseconds;
            if (!response.IsSuccessStatusCode)
            {
                if (Logger is not null)
                {
                    LogRequestFailed(Logger, request.Model, request.RequestId, (int)response.StatusCode, headersElapsedMs);
                }

                throw new LlmUnavailableException(
                    $"Ollama returned {(int)response.StatusCode}. Is the model '{request.Model}' pulled? Run: ollama pull {request.Model}");
            }

            if (Logger is not null)
            {
                LogResponseHeaders(Logger, request.Model, request.RequestId, headersElapsedMs);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var chunk = JsonSerializer.Deserialize<OllamaChatChunk>(line, JsonOptions);
                if (!string.IsNullOrEmpty(chunk?.Message?.Content))
                {
                    yield return chunk.Message.Content;
                }

                if (chunk?.Done == true)
                {
                    yield break;
                }
            }
        }
    }

    private static string RoleName(LlmRole role) => role switch
    {
        LlmRole.System => "system",
        LlmRole.Assistant => "assistant",
        _ => "user",
    };

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ollama request for model {Model} request {RequestId} failed with HTTP {StatusCode} after {ElapsedMs} ms.")]
    private static partial void LogRequestFailed(ILogger logger, string model, Guid? requestId, int? statusCode, double elapsedMs);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ollama response headers for model {Model} request {RequestId} arrived after {ElapsedMs} ms.")]
    private static partial void LogResponseHeaders(ILogger logger, string model, Guid? requestId, double elapsedMs);

    private sealed record OllamaChatRequest(
        string Model,
        IReadOnlyList<OllamaMessage> Messages,
        bool Stream,
        OllamaRequestOptions Options);

    private sealed record OllamaMessage(string Role, string Content);

    private sealed record OllamaRequestOptions([property: JsonPropertyName("temperature")] double Temperature);

    private sealed record OllamaChatChunk(OllamaMessage? Message, bool Done);
}
