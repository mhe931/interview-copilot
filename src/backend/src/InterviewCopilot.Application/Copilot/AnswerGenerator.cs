using System.Runtime.CompilerServices;
using System.Text;
using InterviewCopilot.Application.Common.Exceptions;
using InterviewCopilot.Application.Common.Interfaces;
using InterviewCopilot.Application.Events;
using InterviewCopilot.Application.Providers;
using InterviewCopilot.Domain.Entities;
using InterviewCopilot.Domain.Enums;
using Microsoft.Extensions.Options;

namespace InterviewCopilot.Application.Copilot;

public sealed record AnswerRequest(
    Guid SessionId,
    string Question,
    AnswerModifier Modifier = AnswerModifier.None,
    string? PreviousAnswer = null,
    int PreviousVersion = 0,
    string? ApiKey = null,
    Guid? SegmentId = null,
    Guid? RequestId = null);

public sealed record AnswerHeader(QuestionType QuestionType, int Version, AnswerOrigin Origin, string? SourceDocument);

/// <summary>Detect type → select context → build prompt → stream from the model → persist (FR-08..FR-10).</summary>
public sealed class AnswerGenerator(
    ISessionRepository sessions,
    ITranscriptRepository transcripts,
    IUnitOfWork unitOfWork,
    ILlmProviderResolver llmProviders,
    ISessionKeyStore sessionKeys,
    IOptions<LlmOptions> llmOptions,
    TimeProvider clock,
    ISessionEventPublisher events,
    IUserPreferencesRepository preferences,
    PreparedAnswerIndex? preparedAnswers = null)
{
    public const int RecentSegmentCount = 12;

    public async Task<(AnswerHeader Header, IAsyncEnumerable<string> Tokens)> GenerateAsync(
        AnswerRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var session = await sessions.GetAsync(request.SessionId, cancellationToken)
            ?? throw new NotFoundException("Session not found.");

        var questionType = QuestionTypeResolver.Resolve(
            session.PinnedQuestionType,
            QuestionClassifier.Classify(request.Question));
        var version = request.PreviousVersion + 1;
        var prepared = request.Modifier == AnswerModifier.None
            ? preparedAnswers?.Find(session.Id, session.Documents, request.Question)
            : null;
        if (prepared is not null)
        {
            return (new AnswerHeader(questionType, version, AnswerOrigin.Prepared, prepared.FileName),
                StreamAndPersistAsync(PreparedTokens(prepared.Text, cancellationToken), session.Id, request, questionType,
                    version, AnswerOrigin.Prepared, prepared.FileName, cancellationToken));
        }

        var segments = await transcripts.GetSegmentsAsync(session.Id, cancellationToken);
        var input = new AnswerPromptInput(
            session,
            request.Question,
            questionType,
            DocumentContextSelector.Select(session.Documents, request.Question),
            [.. segments.TakeLast(RecentSegmentCount)],
            request.Modifier,
            request.PreviousAnswer);

        var options = llmOptions.Value;
        var owner = await preferences.GetAsync(session.OwnerId, cancellationToken);
        var providerName = string.IsNullOrWhiteSpace(owner?.LlmProvider) ? options.Provider : owner.LlmProvider;
        var model = string.IsNullOrWhiteSpace(owner?.LlmModel) ? options.Model : owner.LlmModel;
        var llm = llmProviders.Resolve(providerName);

        // A key handed to the live session wins; a per-request key still works for manual answers (#30).
        var apiKey = sessionKeys.TryGet(session.Id, providerName) ?? request.ApiKey;
        var llmRequest = new LlmRequest(model, AnswerPromptBuilder.Build(input), options.Temperature, apiKey);
        return (new AnswerHeader(questionType, version, AnswerOrigin.Generated, null),
            StreamAndPersistAsync(llm.StreamAsync(llmRequest, cancellationToken), session.Id, request, questionType,
                version, AnswerOrigin.Generated, null, cancellationToken));
    }

    private async IAsyncEnumerable<string> StreamAndPersistAsync(
        IAsyncEnumerable<string> tokens,
        Guid sessionId,
        AnswerRequest request,
        QuestionType questionType,
        int version,
        AnswerOrigin origin,
        string? sourceDocument,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var answer = new StringBuilder();
        await foreach (var token in tokens.WithCancellation(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            answer.Append(token);
            events.Publish(sessionId, SessionEventTypes.AnswerDelta, new { token, version, questionType, origin, sourceDocument, segmentId = request.SegmentId, requestId = request.RequestId });
            yield return token;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var suggested = SuggestedAnswer.Create(
            sessionId,
            request.Question,
            questionType,
            answer.ToString(),
            version,
            request.Modifier,
            clock.GetUtcNow(),
            origin,
            sourceDocument);
        transcripts.AddAnswer(suggested);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        events.Publish(sessionId, SessionEventTypes.AnswerDone, new { version, questionType, origin, sourceDocument, answerId = suggested.Id, segmentId = request.SegmentId, requestId = request.RequestId });
    }

    private static async IAsyncEnumerable<string> PreparedTokens(string text, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        yield return text;
    }
}
