using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using InterviewCopilot.Application.Audio;
using InterviewCopilot.Application.Common.Interfaces;
using InterviewCopilot.Application.Events;
using InterviewCopilot.Application.Providers;
using InterviewCopilot.Domain.Entities;
using InterviewCopilot.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace InterviewCopilot.Application.Copilot;

/// <summary>
/// Answers interviewer questions without any typing (#22). Listens to saved transcript segments; for an
/// interviewer question in a live session it publishes <c>question.detected</c> and, unless auto-answer is
/// paused, streams an answer through <see cref="AnswerGenerator"/>. A newer question cancels the in-flight answer.
/// </summary>
public sealed partial class AutoAnswerService : ITranscriptSegmentListener, IDisposable
{
    /// <summary>Longest an automatic answer may run before it is stopped.</summary>
    public static readonly TimeSpan DefaultAnswerTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Sent to clients when an automatic answer fails for a reason that isn't a user-facing provider error.</summary>
    public const string GenericFailureMessage = "The automatic answer couldn't be generated.";

    private static readonly TimeSpan DuplicateQuestionWindow = TimeSpan.FromSeconds(8);

    private readonly IServiceScopeFactory _scopes;
    private readonly ISessionEventPublisher _events;
    private readonly ILogger<AutoAnswerService> _logger;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _answerTimeout;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, InFlight> _inFlight = [];
    private readonly Dictionary<Guid, HashSet<InFlight>> _running = [];
    private readonly Dictionary<Guid, RecentQuestion> _recentQuestions = [];

    /// <summary>Creates the service.</summary>
    /// <param name="scopes">Creates a scope per answer for the scoped repositories and generator.</param>
    /// <param name="events">Where question and error events go.</param>
    /// <param name="logger">Logger; never receives transcript or answer text.</param>
    /// <param name="answerTimeout">Per-answer time limit; defaults to <see cref="DefaultAnswerTimeout"/>.</param>
    public AutoAnswerService(
        IServiceScopeFactory scopes,
        ISessionEventPublisher events,
        ILogger<AutoAnswerService> logger,
        TimeSpan? answerTimeout = null,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(logger);
        _scopes = scopes;
        _events = events;
        _logger = logger;
        _answerTimeout = answerTimeout ?? DefaultAnswerTimeout;
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async ValueTask OnSegmentAsync(TranscriptSegment segment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (segment.Speaker != Speaker.Interviewer)
        {
            return;
        }

        var detectionStartedAt = Stopwatch.GetTimestamp();
        var isQuestion = QuestionDetector.IsQuestion(segment.Text);
        LogQuestionDetection(segment.SessionId, ElapsedMilliseconds(detectionStartedAt), isQuestion);
        if (!isQuestion)
        {
            return;
        }

        bool autoAnswer;
        using (var scope = _scopes.CreateScope())
        {
            var session = await scope.ServiceProvider.GetRequiredService<ISessionRepository>()
                .GetAsync(segment.SessionId, cancellationToken);
            if (session is null || session.Status != SessionStatus.Live)
            {
                return;
            }

            autoAnswer = session.AutoAnswer;
        }

        if (!TryRememberQuestion(segment.SessionId, segment.Text))
        {
            return;
        }

        // Publish first: clients reset their answer card on question.detected, so it must precede the answer's deltas.
        _events.Publish(segment.SessionId, SessionEventTypes.QuestionDetected, new { segmentId = segment.Id, text = segment.Text });

        // A newer question replaces whatever is still being answered, even when auto-answer is now paused.
        Replace(segment.SessionId, segment.Id, autoAnswer ? segment.Text : null);
    }

    /// <summary>Completes when the session's current automatic answer (if any) has finished or been cancelled.</summary>
    /// <param name="sessionId">The session to wait for.</param>
    /// <returns>A task that completes when no answer is running for the session.</returns>
    public async Task WhenIdleAsync(Guid sessionId)
    {
        while (true)
        {
            Task[] running;
            lock (_gate)
            {
                running = _running.TryGetValue(sessionId, out var current) ? [.. current.Select(e => e.Task)] : [];
            }

            if (running.Length == 0)
            {
                return;
            }

            await Task.WhenAll(running);
        }
    }

    public async Task CancelAsync(Guid sessionId, string? suppressDuplicateQuestion = null)
    {
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(suppressDuplicateQuestion))
            {
                RememberQuestionLocked(sessionId, suppressDuplicateQuestion);
            }

            if (_running.TryGetValue(sessionId, out var running))
            {
                foreach (var entry in running)
                {
                    entry.Cancel();
                }
            }
        }

        await WhenIdleAsync(sessionId);
    }

    public async Task CancelCurrentAsync(Guid sessionId, Guid segmentId)
    {
        Task? currentTask = null;
        lock (_gate)
        {
            if (_inFlight.TryGetValue(sessionId, out var current) && current.SegmentId == segmentId)
            {
                current.Cancel();
                currentTask = current.Task;
            }
        }

        if (currentTask is not null)
        {
            _events.Publish(sessionId, SessionEventTypes.AnswerCancelled, new { segmentId });
            await currentTask;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var entry in _running.Values.SelectMany(entries => entries))
            {
                entry.Cancel();
            }

            _recentQuestions.Clear();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Automatic answer failed for session {SessionId} ({ErrorType}).")]
    private partial void LogAnswerFailed(Guid sessionId, string errorType);

    [LoggerMessage(Level = LogLevel.Information, Message = "Automatic answer for session {SessionId} timed out during {Phase}.")]
    private partial void LogAnswerTimedOut(Guid sessionId, AnswerTimeoutPhase phase);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Interviewer question detection for session {SessionId} took {ElapsedMs} ms; matched={Matched}.")]
    private partial void LogQuestionDetection(Guid sessionId, double elapsedMs, bool matched);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Automatic answer for session {SessionId} was cancelled by a newer request or session stop.")]
    private partial void LogAnswerCancelled(Guid sessionId);

    private static double ElapsedMilliseconds(long startedAt) => Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

    /// <summary>Stops the session's running answer and, when a question is given, starts answering it.</summary>
    private void Replace(Guid sessionId, Guid segmentId, string? question)
    {
        lock (_gate)
        {
            if (_inFlight.Remove(sessionId, out var previous))
            {
                previous.Cancel();
            }

            if (question is null)
            {
                return;
            }

            var entry = new InFlight(segmentId);
            entry.Task = Task.Run(() => RunAsync(sessionId, segmentId, question, entry), CancellationToken.None);
            _inFlight[sessionId] = entry;
            if (!_running.TryGetValue(sessionId, out var running))
            {
                running = [];
                _running[sessionId] = running;
            }

            running.Add(entry);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A failed background answer is reported as an error event and must not crash the host.")]
    private async Task RunAsync(Guid sessionId, Guid segmentId, string question, InFlight self)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var generator = scope.ServiceProvider.GetRequiredService<AnswerGenerator>();
            var (_, tokens) = await generator.GenerateAsync(
                new AnswerRequest(sessionId, question, SegmentId: segmentId, TotalTimeout: _answerTimeout),
                self.Token);
            await foreach (var token in tokens.WithCancellation(self.Token))
            {
                _ = token; // Consuming the stream is what publishes answer.delta / answer.done.
            }
        }
        catch (AnswerStreamTimeoutException ex)
        {
            LogAnswerTimedOut(sessionId, ex.Phase);
            _events.Publish(sessionId, SessionEventTypes.Error, new { message = ex.Message, segmentId });
        }
        catch (OperationCanceledException) when (self.CancelledByCaller)
        {
            LogAnswerCancelled(sessionId);
        }
        catch (OperationCanceledException ex)
        {
            LogAnswerFailed(sessionId, ex.GetType().Name);
            _events.Publish(sessionId, SessionEventTypes.Error, new { message = GenericFailureMessage, segmentId });
        }
        catch (LlmUnavailableException ex)
        {
            _events.Publish(sessionId, SessionEventTypes.Error, new { message = ex.Message, segmentId });
        }
        catch (Exception ex)
        {
            LogAnswerFailed(sessionId, ex.GetType().Name);
            _events.Publish(sessionId, SessionEventTypes.Error, new { message = GenericFailureMessage, segmentId });
        }
        finally
        {
            lock (_gate)
            {
                if (_inFlight.TryGetValue(sessionId, out var current) && ReferenceEquals(current, self))
                {
                    _inFlight.Remove(sessionId);
                }

                if (_running.TryGetValue(sessionId, out var running) && running.Remove(self) && running.Count == 0)
                {
                    _running.Remove(sessionId);
                }

                self.Dispose();
            }
        }
    }

    private bool TryRememberQuestion(Guid sessionId, string question)
    {
        lock (_gate)
        {
            return RememberQuestionLocked(sessionId, question);
        }
    }

    private bool RememberQuestionLocked(Guid sessionId, string question)
    {
        var now = _clock.GetUtcNow();
        foreach (var expired in _recentQuestions.Where(entry => entry.Value.ExpiresAt <= now).Select(entry => entry.Key).ToArray())
        {
            _recentQuestions.Remove(expired);
        }

        var normalized = NormalizeQuestion(question);
        if (normalized.Length == 0)
        {
            return true;
        }

        if (_recentQuestions.TryGetValue(sessionId, out var recent)
            && recent.ExpiresAt > now
            && recent.Normalized == normalized)
        {
            return false;
        }

        _recentQuestions[sessionId] = new RecentQuestion(normalized, now + DuplicateQuestionWindow);
        return true;
    }

    private static string NormalizeQuestion(string question) =>
        string.Concat(question.Where(char.IsLetterOrDigit)).ToUpperInvariant();

    private sealed record RecentQuestion(string Normalized, DateTimeOffset ExpiresAt);

    private sealed class InFlight : IDisposable
    {
        private readonly CancellationTokenSource _source = new();

        public InFlight(Guid segmentId) => SegmentId = segmentId;

        public Guid SegmentId { get; }

        public CancellationToken Token => _source.Token;

        public bool CancelledByCaller { get; private set; }

        public Task Task { get; set; } = Task.CompletedTask;

        public void Cancel()
        {
            CancelledByCaller = true;
            _source.Cancel();
        }

        public void Dispose() => _source.Dispose();
    }
}
