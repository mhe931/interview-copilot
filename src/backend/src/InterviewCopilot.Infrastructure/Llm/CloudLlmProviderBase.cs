using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using InterviewCopilot.Application.Providers;
using Microsoft.Extensions.Logging;

namespace InterviewCopilot.Infrastructure.Llm;

/// <summary>One decoded server-sent event of a cloud chat stream.</summary>
/// <param name="Text">Text to append to the answer, if the event carried any.</param>
/// <param name="Done">True when the provider says the answer is complete.</param>
internal readonly record struct CloudChunk(string? Text, bool Done);

/// <summary>
/// Shared plumbing for the BYOK chat providers (#30): key check, streaming request, SSE reading and error mapping.
/// Messages are user-facing and never contain the API key (it only travels in a request header).
/// </summary>
internal abstract partial class CloudLlmProviderBase(HttpClient http, ILogger? logger = null) : ILlmProvider
{
    public abstract string Name { get; }

    /// <summary>Provider name as shown to the user in error messages.</summary>
    protected abstract string DisplayName { get; }

    protected ILogger? Logger { get; } = logger;

    public IAsyncEnumerable<string> StreamAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return StreamCoreAsync(request, cancellationToken);
    }

    protected abstract HttpRequestMessage BuildMessage(LlmRequest request, string apiKey);

    protected abstract CloudChunk Parse(string data);

    protected virtual string ErrorMessage(HttpResponseMessage response) =>
        CloudProviderErrors.FromStatus(DisplayName, response.StatusCode);

    private async IAsyncEnumerable<string> StreamCoreAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ApiKey))
        {
            throw new LlmUnavailableException(CloudProviderErrors.MissingKey(DisplayName));
        }

        using var message = BuildMessage(request, request.ApiKey);
        var requestStartedAt = Stopwatch.GetTimestamp();
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new LlmUnavailableException(CloudProviderErrors.Unreachable(DisplayName), ex);
        }

        using (response)
        {
            var headersElapsedMs = Stopwatch.GetElapsedTime(requestStartedAt).TotalMilliseconds;
            if (!response.IsSuccessStatusCode)
            {
                if (Logger is not null)
                {
                    LogRequestFailed(Logger, Name, request.Model, request.RequestId, (int)response.StatusCode, headersElapsedMs);
                }

                throw new LlmUnavailableException(ErrorMessage(response));
            }

            if (Logger is not null)
            {
                LogResponseHeaders(Logger, Name, request.Model, request.RequestId, headersElapsedMs);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);
            var firstTextLogged = false;
            var completed = false;
            try
            {
                while (await reader.ReadLineAsync(cancellationToken) is { } line)
                {
                    if (!line.StartsWith("data:", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var data = line["data:".Length..].Trim();
                    if (data.Length == 0)
                    {
                        continue;
                    }

                    CloudChunk chunk;
                    try
                    {
                        chunk = Parse(data);
                    }
                    catch (JsonException ex)
                    {
                        throw new LlmUnavailableException(CloudProviderErrors.Unreadable(DisplayName), ex);
                    }

                    if (!string.IsNullOrEmpty(chunk.Text))
                    {
                        if (!firstTextLogged)
                        {
                            firstTextLogged = true;
                            if (Logger is not null)
                            {
                                LogFirstText(
                                    Logger,
                                    Name,
                                    request.Model,
                                    request.RequestId,
                                    Stopwatch.GetElapsedTime(requestStartedAt).TotalMilliseconds);
                            }
                        }

                        yield return chunk.Text;
                    }

                    if (chunk.Done)
                    {
                        completed = true;
                        yield break;
                    }
                }

                completed = true;
            }
            finally
            {
                if (Logger is not null)
                {
                    LogStreamEnded(
                        Logger,
                        Name,
                        request.Model,
                        request.RequestId,
                        completed ? "completed" : "stopped",
                        Stopwatch.GetElapsedTime(requestStartedAt).TotalMilliseconds);
                }
            }
        }
    }

    protected static string RoleName(LlmRole role) => role switch
    {
        LlmRole.System => "system",
        LlmRole.Assistant => "assistant",
        _ => "user",
    };

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cloud LLM request for {Provider} model {Model} request {RequestId} returned HTTP {StatusCode} after {ElapsedMs} ms.")]
    private static partial void LogRequestFailed(ILogger logger, string provider, string model, Guid? requestId, int statusCode, double elapsedMs);

    [LoggerMessage(Level = LogLevel.Information, Message = "Cloud LLM response headers for {Provider} model {Model} request {RequestId} arrived after {ElapsedMs} ms.")]
    private static partial void LogResponseHeaders(ILogger logger, string provider, string model, Guid? requestId, double elapsedMs);

    [LoggerMessage(Level = LogLevel.Information, Message = "First text from {Provider} model {Model} request {RequestId} arrived after {ElapsedMs} ms.")]
    private static partial void LogFirstText(ILogger logger, string provider, string model, Guid? requestId, double elapsedMs);

    [LoggerMessage(Level = LogLevel.Information, Message = "Cloud LLM stream for {Provider} model {Model} request {RequestId} {Outcome} after {ElapsedMs} ms.")]
    private static partial void LogStreamEnded(ILogger logger, string provider, string model, Guid? requestId, string outcome, double elapsedMs);
}

/// <summary>User-facing messages for cloud providers. None of them may include the key.</summary>
internal static class CloudProviderErrors
{
    public static string MissingKey(string provider) => $"Add your {provider} API key in Settings.";

    public static string Rejected(string provider) => $"{provider} rejected the API key.";

    public static string RateLimited(string provider) => $"{provider} is rate limiting requests. Try again shortly.";

    public static string Unreachable(string provider) => $"Can't reach {provider}. Check your internet connection.";

    public static string Unreadable(string provider) => $"{provider} sent a reply that couldn't be read.";

    public static string FromGoogleStatus(HttpResponseMessage response)
    {
        var provider = "Google Gemini";
        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => Rejected(provider),
            HttpStatusCode.Forbidden =>
                "Google Gemini denied the request. Check the API key's permissions, model access, and billing status.",
            HttpStatusCode.BadRequest =>
                "Google Gemini rejected the request. Check that the selected model supports the requested generation settings.",
            HttpStatusCode.NotFound =>
                "Google Gemini could not find this model or it is unavailable to the current API key.",
            HttpStatusCode.TooManyRequests => GoogleRateLimited(response),
            HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout =>
                "Google Gemini is temporarily unavailable. Try again shortly.",
            _ => $"{provider} returned an error ({(int)response.StatusCode}). Check the model and provider status.",
        };
    }

    public static string FromGoogleStreamError(int? statusCode)
    {
        if (statusCode is not (>= 100 and <= 599))
        {
            return "Google Gemini reported an error while streaming the response. Check model access, quota, and API status.";
        }

        using var response = new HttpResponseMessage((HttpStatusCode)statusCode.Value);
        return FromGoogleStatus(response);
    }

    public static string FromStatus(string provider, HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => Rejected(provider),
        HttpStatusCode.TooManyRequests => RateLimited(provider),
        _ => $"{provider} returned an error ({(int)status}). Try again shortly.",
    };

    private static string GoogleRateLimited(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter?.Delta;
        if (retryAfter is null && response.Headers.RetryAfter?.Date is { } date)
        {
            retryAfter = date - DateTimeOffset.UtcNow;
        }

        return retryAfter is { } delay && delay > TimeSpan.Zero
            ? $"Google Gemini quota or rate limit reached. Check the key's quota and retry after about {Math.Ceiling(delay.TotalSeconds)} seconds."
            : "Google Gemini quota or rate limit reached. Check the key's quota before retrying.";
    }
}
