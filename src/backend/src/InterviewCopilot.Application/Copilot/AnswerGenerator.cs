using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using InterviewCopilot.Application.Common.Exceptions;
using InterviewCopilot.Application.Common.Interfaces;
using InterviewCopilot.Application.Events;
using InterviewCopilot.Application.Providers;
using InterviewCopilot.Domain.Entities;
using InterviewCopilot.Domain.Enums;
using Microsoft.Extensions.Logging;
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
    Guid? RequestId = null,
    TimeSpan? TotalTimeout = null);

public sealed record AnswerHeader(
    QuestionType QuestionType,
    int Version,
    AnswerOrigin Origin,
    string? SourceDocument,
    double PreparedAnswerLookupMs);

/// <summary>Detect type → select context → build prompt → stream from the model → persist (FR-08..FR-10).</summary>
public sealed partial class AnswerGenerator(
    ISessionRepository sessions,
    ITranscriptRepository transcripts,
    IUnitOfWork unitOfWork,
    ILlmProviderResolver llmProviders,
    ISessionKeyStore sessionKeys,
    IOptions<LlmOptions> llmOptions,
    TimeProvider clock,
    ISessionEventPublisher events,
    IUserPreferencesRepository preferences,
    ILogger<AnswerGenerator> logger,
    PreparedAnswerIndex? preparedAnswers = null,
    AnswerTimeoutOptions? timeoutOptions = null)
{
    public const int RecentSegmentCount = 12;

    private readonly AnswerTimeoutOptions _timeoutOptions = timeoutOptions ?? new();
    private readonly ILogger<AnswerGenerator> _logger = logger;

    public async Task<(AnswerHeader Header, IAsyncEnumerable<string> Tokens)> GenerateAsync(
        AnswerRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var generationStartedAt = Stopwatch.GetTimestamp();
        var session = await sessions.GetAsync(request.SessionId, cancellationToken)
            ?? throw new NotFoundException("Session not found.");

        var questionType = QuestionTypeResolver.Resolve(
            session.PinnedQuestionType,
            QuestionClassifier.Classify(request.Question));
        var version = request.PreviousVersion + 1;
        var preparedLookupStartedAt = Stopwatch.GetTimestamp();
        var prepared = request.Modifier == AnswerModifier.None
            ? preparedAnswers?.Find(session.Id, session.Documents, request.Question)
            : null;
        var totalTimeout = request.TotalTimeout ?? _timeoutOptions.Total;
        var timeouts = _timeoutOptions;
        var preparedLookupMs = ElapsedMilliseconds(preparedLookupStartedAt);
        LogPreparedAnswerLookup(TraceId(request), preparedLookupMs);
        if (prepared is not null)
        {
            return (new AnswerHeader(questionType, version, AnswerOrigin.Prepared, prepared.FileName, preparedLookupMs),
                StreamAndPersistAsync(
                    ct => PreparedTokens(prepared.Text, ct),
                    session.Id,
                    request,
                    questionType,
                    version,
                    AnswerOrigin.Prepared,
                    prepared.FileName,
                    timeouts,
                    totalTimeout,
                    generationStartedAt,
                    contextPreparationMs: 0,
                    cancellationToken));
        }

        var contextStartedAt = Stopwatch.GetTimestamp();
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
        var llmRequest = new LlmRequest(model, AnswerPromptBuilder.Build(input), options.Temperature, apiKey, TraceId(request));
        var contextPreparationMs = ElapsedMilliseconds(contextStartedAt);
        LogContextPrepared(TraceId(request), contextPreparationMs);
        return (new AnswerHeader(questionType, version, AnswerOrigin.Generated, null, preparedLookupMs),
            StreamAndPersistAsync(
                ct => llm.StreamAsync(llmRequest, ct),
                session.Id,
                request,
                questionType,
                version,
                AnswerOrigin.Generated,
                null,
                timeouts,
                totalTimeout,
                generationStartedAt,
                contextPreparationMs,
                cancellationToken));
    }

    private async IAsyncEnumerable<string> StreamAndPersistAsync(
        Func<CancellationToken, IAsyncEnumerable<string>> tokens,
        Guid sessionId,
        AnswerRequest request,
        QuestionType questionType,
        int version,
        AnswerOrigin origin,
        string? sourceDocument,
        AnswerTimeoutOptions timeouts,
        TimeSpan totalTimeout,
        long generationStartedAt,
        double contextPreparationMs,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var answer = new StringBuilder();
        var firstTokenLogged = false;
        await foreach (var token in StreamWithTimeoutsAsync(
            tokens,
            timeouts,
            totalTimeout,
            generationStartedAt,
            cancellationToken).WithCancellation(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            answer.Append(token);
            if (!firstTokenLogged && token.Length > 0)
            {
                firstTokenLogged = true;
                LogFirstToken(TraceId(request), ElapsedMilliseconds(generationStartedAt));
            }

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
        LogAnswerCompleted(TraceId(request), ElapsedMilliseconds(generationStartedAt), contextPreparationMs);
    }

    private static async IAsyncEnumerable<string> StreamWithTimeoutsAsync(
        Func<CancellationToken, IAsyncEnumerable<string>> tokens,
        AnswerTimeoutOptions timeouts,
        TimeSpan totalTimeout,
        long generationStartedAt,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var streamCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var enumerator = tokens(streamCancellation.Token).GetAsyncEnumerator(streamCancellation.Token);
        var receivedText = false;
        try
        {
            while (true)
            {
                var remainingTotal = totalTimeout - Stopwatch.GetElapsedTime(generationStartedAt);
                if (remainingTotal <= TimeSpan.Zero)
                {
                    await streamCancellation.CancelAsync();
                    throw new AnswerStreamTimeoutException(AnswerTimeoutPhase.Total);
                }

                var phase = receivedText ? AnswerTimeoutPhase.IdleStream : AnswerTimeoutPhase.FirstToken;
                var phaseTimeout = receivedText ? timeouts.IdleStream : timeouts.FirstToken;
                var waitTimeout = remainingTotal < phaseTimeout ? remainingTotal : phaseTimeout;
                using var phaseCancellation = new CancellationTokenSource(waitTimeout);
                using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    streamCancellation.Token,
                    phaseCancellation.Token);

                bool hasNext;
                var moveNext = enumerator.MoveNextAsync().AsTask();
                try
                {
                    hasNext = await moveNext.WaitAsync(waitCancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    await streamCancellation.CancelAsync();
                    await SettleMoveNextAsync(moveNext, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();

                    var timedOutPhase = Stopwatch.GetElapsedTime(generationStartedAt) >= totalTimeout
                        ? AnswerTimeoutPhase.Total
                        : phase;
                    throw new AnswerStreamTimeoutException(timedOutPhase);
                }

                if (!hasNext)
                {
                    yield break;
                }

                var token = enumerator.Current;
                receivedText |= token.Length > 0;
                yield return token;
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    private static async Task SettleMoveNextAsync(Task<bool> moveNext, CancellationToken cancellationToken)
    {
        try
        {
            _ = await moveNext;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static async IAsyncEnumerable<string> PreparedTokens(string text, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        yield return text;
    }

    private static Guid? TraceId(AnswerRequest request) => request.RequestId ?? request.SegmentId;

    private static double ElapsedMilliseconds(long startedAt) => Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

    [LoggerMessage(Level = LogLevel.Information, Message = "Prepared-answer lookup for {RequestId} took {ElapsedMs} ms.")]
    private partial void LogPreparedAnswerLookup(Guid? requestId, double elapsedMs);

    [LoggerMessage(Level = LogLevel.Information, Message = "Answer context preparation for {RequestId} took {ElapsedMs} ms.")]
    private partial void LogContextPrepared(Guid? requestId, double elapsedMs);

    [LoggerMessage(Level = LogLevel.Information, Message = "First answer text for {RequestId} arrived after {ElapsedMs} ms.")]
    private partial void LogFirstToken(Guid? requestId, double elapsedMs);

    [LoggerMessage(Level = LogLevel.Information, Message = "Answer for {RequestId} completed in {ElapsedMs} ms; context preparation took {ContextPreparationMs} ms.")]
    private partial void LogAnswerCompleted(Guid? requestId, double elapsedMs, double contextPreparationMs);
}
