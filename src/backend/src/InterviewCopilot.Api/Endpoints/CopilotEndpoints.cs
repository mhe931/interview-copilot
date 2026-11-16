using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using InterviewCopilot.Api.Infrastructure;
using InterviewCopilot.Application.Copilot;
using InterviewCopilot.Application.Events;
using InterviewCopilot.Application.Providers;
using InterviewCopilot.Application.Sessions;
using InterviewCopilot.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace InterviewCopilot.Api.Endpoints;

public sealed record AnswerQuestionRequest(
    string Question,
    AnswerModifier Modifier = AnswerModifier.None,
    string? PreviousAnswer = null,
    int PreviousVersion = 0,
    Guid? RequestId = null);

internal static class CopilotEndpoints
{
    /// <summary>BYOK key header. The key is used for this request only; never logged or stored.</summary>
    public const string LlmApiKeyHeader = "X-Llm-Api-Key";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static void MapCopilotEndpoints(this IEndpointRouteBuilder app)
    {
        // Streams: "meta" (type + version), many "delta" (tokens), then "done" or "error" (FR-09, FR-10).
        app.MapPost("/api/sessions/{sessionId:guid}/answers", async (
                Guid sessionId,
                AnswerQuestionRequest request,
                [FromHeader(Name = LlmApiKeyHeader)] string? apiKey,
                ClaimsPrincipal user,
                SessionService sessions,
                AnswerGenerator generator,
                AutoAnswerService autoAnswers,
                ISessionEventPublisher events,
                CancellationToken ct) =>
            {
                await sessions.GetAsync(user.UserId(), sessionId, ct); // ownership check, 404 otherwise
                await autoAnswers.CancelAsync(sessionId, request.Question);
                var requestId = request.RequestId ?? Guid.NewGuid();
                var (header, tokens) = await generator.GenerateAsync(
                    new AnswerRequest(sessionId, request.Question, request.Modifier, request.PreviousAnswer, request.PreviousVersion, apiKey, RequestId: requestId),
                    ct);
                events.Publish(sessionId, SessionEventTypes.AnswerStarted, new { requestId, question = request.Question });
                return TypedResults.ServerSentEvents(ToEvents(header, tokens, sessionId, requestId, events, ct));
            })
            .WithTags("Copilot")
            .RequireAuthorization(Policies.ExtensionScope);

        app.MapPost("/api/sessions/{sessionId:guid}/answers/{segmentId:guid}/cancel", async (
                Guid sessionId,
                Guid segmentId,
                ClaimsPrincipal user,
                SessionService sessions,
                AutoAnswerService autoAnswers,
                CancellationToken ct) =>
            {
                await sessions.GetAsync(user.UserId(), sessionId, ct);
                await autoAnswers.CancelCurrentAsync(sessionId, segmentId);
                return TypedResults.NoContent();
            })
            .WithTags("Copilot")
            .RequireAuthorization(Policies.ExtensionScope);
    }

    private static async IAsyncEnumerable<SseItem<string>> ToEvents(
        AnswerHeader header,
        IAsyncEnumerable<string> tokens,
        Guid sessionId,
        Guid requestId,
        ISessionEventPublisher events,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var enumerator = tokens.GetAsyncEnumerator(cancellationToken);
        string? error = null;
        var completed = false;
        try
        {
            yield return new SseItem<string>(JsonSerializer.Serialize(header, JsonOptions), "meta");
            while (true)
            {
                string token;
                try
                {
                    if (!await enumerator.MoveNextAsync())
                    {
                        completed = true;
                        break;
                    }

                    token = enumerator.Current;
                }
                catch (LlmUnavailableException ex)
                {
                    error = ex.Message;
                    events.Publish(sessionId, SessionEventTypes.Error, new { requestId, message = error });
                    completed = true;
                    break;
                }
                catch (AnswerStreamTimeoutException ex)
                {
                    error = ex.Message;
                    events.Publish(sessionId, SessionEventTypes.Error, new { requestId, message = error });
                    completed = true;
                    break;
                }

                yield return new SseItem<string>(token, "delta");
            }
        }
        finally
        {
            try
            {
                await enumerator.DisposeAsync();
            }
            finally
            {
                if (!completed)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        events.Publish(sessionId, SessionEventTypes.AnswerCancelled, new { requestId });
                    }
                    else
                    {
                        events.Publish(sessionId, SessionEventTypes.Error, new { requestId, message = "The answer was interrupted." });
                    }
                }
            }
        }

        yield return error is null
            ? new SseItem<string>(string.Empty, "done")
            : new SseItem<string>(error, "error");
    }
}
