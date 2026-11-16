using InterviewCopilot.Application.Audio;
using InterviewCopilot.Application.Common.Exceptions;
using InterviewCopilot.Application.Common.Interfaces;
using InterviewCopilot.Application.Copilot;
using InterviewCopilot.Application.Events;
using InterviewCopilot.Application.Practice;
using InterviewCopilot.Application.Providers;
using InterviewCopilot.Domain.Entities;
using InterviewCopilot.Domain.Enums;
using InterviewCopilot.Domain.Exceptions;

namespace InterviewCopilot.Application.Sessions;

public sealed record CreateSessionCommand(
    Guid OwnerId,
    string Title,
    SessionMode Mode,
    AnswerLength AnswerLength,
    AnswerFramework Framework,
    QuestionType? PinnedQuestionType,
    string? SessionPrompt,
    Guid? CopyFromSessionId = null);

public sealed record UpdateSessionSettingsCommand(
    AnswerLength AnswerLength,
    AnswerFramework Framework,
    QuestionType? PinnedQuestionType,
    string? SessionPrompt);

public sealed record DocumentSummaryDto(Guid Id, string FileName, int CharacterCount, DateTimeOffset CreatedAt);

public sealed record SessionDto(
    Guid Id,
    string Title,
    SessionMode Mode,
    SessionStatus Status,
    AnswerLength AnswerLength,
    AnswerFramework Framework,
    QuestionType? PinnedQuestionType,
    string SessionPrompt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    IReadOnlyList<DocumentSummaryDto> Documents,
    bool AutoAnswer,
    long AccumulatedDurationMs = 0)
{
    public static SessionDto From(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new SessionDto(
            session.Id,
            session.Title,
            session.Mode,
            session.Status,
            session.AnswerLength,
            session.Framework,
            session.PinnedQuestionType,
            session.SessionPrompt,
            session.CreatedAt,
            session.StartedAt,
            session.EndedAt,
            [.. session.Documents.Select(d => new DocumentSummaryDto(d.Id, d.FileName, d.Content.Length, d.CreatedAt))],
            session.AutoAnswer,
            session.AccumulatedDurationMs);
    }
}

/// <summary>Session setup and lifecycle (FR-01..FR-06).</summary>
public sealed class SessionService(
    ISessionRepository sessions,
    IUnitOfWork unitOfWork,
    TimeProvider clock,
    ISessionEventPublisher? events = null,
    ISessionKeyStore? sessionKeys = null,
    SessionRuntime? runtime = null,
    IEnumerable<IAudioFrameSink>? audioSinks = null,
    AutoAnswerService? autoAnswers = null,
    PracticeAnswerCollector? practiceAnswers = null,
    PreparedAnswerIndex? preparedAnswers = null)
{
    public const int MaxApiKeyLength = 512;

    public const string EndBeforeDeletingMessage = "End the session before deleting it.";

    public async Task<SessionDto> CreateAsync(CreateSessionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var now = clock.GetUtcNow();
        Session session;
        if (command.CopyFromSessionId is { } sourceId)
        {
            // #16: a copy takes everything but the title from the source, which must be the caller's own.
            var source = await LoadOwnedAsync(command.OwnerId, sourceId, cancellationToken);
            session = Session.Create(command.OwnerId, command.Title, source.Mode, now);
            session.ConfigureAnswers(source.AnswerLength, source.Framework, source.PinnedQuestionType, source.SessionPrompt);
            session.SetAutoAnswer(source.AutoAnswer);
            foreach (var document in source.Documents)
            {
                session.AttachDocument(document.FileName, document.Content, now);
            }
        }
        else
        {
            session = Session.Create(command.OwnerId, command.Title, command.Mode, now);
            session.ConfigureAnswers(command.AnswerLength, command.Framework, command.PinnedQuestionType, command.SessionPrompt);
        }

        sessions.Add(session);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return SessionDto.From(session);
    }

    public async Task<SessionDto> GetAsync(Guid ownerId, Guid sessionId, CancellationToken cancellationToken) =>
        SessionDto.From(await LoadOwnedAsync(ownerId, sessionId, cancellationToken));

    public async Task<IReadOnlyList<SessionDto>> ListAsync(Guid ownerId, CancellationToken cancellationToken)
    {
        var owned = await sessions.ListByOwnerAsync(ownerId, cancellationToken);
        return [.. owned.Select(SessionDto.From)];
    }

    public async Task<SessionDto> UpdateSettingsAsync(
        Guid ownerId,
        Guid sessionId,
        UpdateSessionSettingsCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        using var transition = runtime is null ? null : await runtime.EnterAsync(sessionId, cancellationToken);
        var session = await LoadOwnedAsync(ownerId, sessionId, cancellationToken);
        session.ConfigureAnswers(command.AnswerLength, command.Framework, command.PinnedQuestionType, command.SessionPrompt);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        preparedAnswers?.Prepare(session.Id, session.Documents);
        return SessionDto.From(session);
    }

    public async Task<SessionDto> StartAsync(Guid ownerId, Guid sessionId, CancellationToken cancellationToken)
    {
        using var transition = runtime is null ? null : await runtime.EnterAsync(sessionId, cancellationToken);
        var session = await LoadOwnedAsync(ownerId, sessionId, cancellationToken);
        if (session.Status == SessionStatus.Live)
        {
            throw new ConflictException("The session is already live.");
        }

        await SettleRuntimeAsync(session.Id);
        foreach (var stats in (audioSinks ?? []).OfType<AudioStatsSink>())
        {
            stats.Reset(session.Id);
        }

        preparedAnswers?.Prepare(session.Id, session.Documents);
        session.Start(clock.GetUtcNow());
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return SessionDto.From(session);
    }

    /// <summary>
    /// Permanently deletes one of the owner's sessions with its documents, transcript, answers and practice attempts
    /// (#80). A live session must be ended first.
    /// </summary>
    /// <param name="ownerId">The signed-in user.</param>
    /// <param name="sessionId">The session to delete.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <returns>A task that completes once the session is gone.</returns>
    public async Task DeleteAsync(Guid ownerId, Guid sessionId, CancellationToken cancellationToken)
    {
        using var transition = runtime is null ? null : await runtime.EnterAsync(sessionId, cancellationToken);
        var session = await LoadOwnedAsync(ownerId, sessionId, cancellationToken);
        if (session.Status == SessionStatus.Live)
        {
            throw new ConflictException(EndBeforeDeletingMessage);
        }

        sessions.Remove(session);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        preparedAnswers?.Remove(sessionId);
    }

    public async Task<SessionDto> EndAsync(Guid ownerId, Guid sessionId, CancellationToken cancellationToken)
    {
        using var transition = runtime is null ? null : await runtime.EnterAsync(sessionId, cancellationToken);
        var session = await LoadOwnedAsync(ownerId, sessionId, cancellationToken);
        session.End(clock.GetUtcNow());
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await SettleRuntimeAsync(session.Id);
        preparedAnswers?.Remove(sessionId);
        return SessionDto.From(session);
    }

    private async Task SettleRuntimeAsync(Guid sessionId)
    {
        if (runtime is not null)
        {
            await runtime.StopStreamAsync(sessionId);
        }

        foreach (var sink in audioSinks ?? [])
        {
            await sink.OnStreamEndedAsync(sessionId, CancellationToken.None);
        }

        foreach (var sink in audioSinks ?? [])
        {
            await sink.WaitForIdleAsync(sessionId, CancellationToken.None);
        }

        if (autoAnswers is not null)
        {
            await autoAnswers.CancelAsync(sessionId);
        }

        practiceAnswers?.Take(sessionId);
        sessionKeys?.Clear(sessionId);
    }

    /// <summary>
    /// Hands a BYOK key to a live session so backend-side work (auto-answer, Deepgram) can use it. Kept in
    /// memory only and cleared when the session ends (wave-3 owner decision).
    /// </summary>
    public async Task SetSessionKeyAsync(Guid ownerId, Guid sessionId, string provider, string key, CancellationToken cancellationToken)
    {
        if (!ProviderNames.KeyedProviders.Contains(provider))
        {
            throw new DomainException("That provider doesn't take an API key.");
        }

        if (string.IsNullOrWhiteSpace(key) || key.Length > MaxApiKeyLength)
        {
            throw new DomainException($"API keys must be 1 to {MaxApiKeyLength} characters.");
        }

        using var transition = runtime is null ? null : await runtime.EnterAsync(sessionId, cancellationToken);
        var session = await LoadOwnedAsync(ownerId, sessionId, cancellationToken);
        if (session.Status != SessionStatus.Live)
        {
            throw new ConflictException("Keys can only be handed to a live session.");
        }

        (sessionKeys ?? throw new InvalidOperationException("No session key store is registered."))
            .Store(session.Id, provider, key.Trim());
    }

    /// <summary>Pauses or resumes automatic answers and tells everyone watching the session (#22).</summary>
    public async Task<SessionDto> SetAutoAnswerAsync(Guid ownerId, Guid sessionId, bool enabled, CancellationToken cancellationToken)
    {
        using var transition = runtime is null ? null : await runtime.EnterAsync(sessionId, cancellationToken);
        var session = await LoadOwnedAsync(ownerId, sessionId, cancellationToken);
        session.SetAutoAnswer(enabled);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        events?.Publish(session.Id, SessionEventTypes.AutoAnswerState, new { enabled });
        return SessionDto.From(session);
    }

    internal async Task<Session> LoadOwnedAsync(Guid ownerId, Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await sessions.GetAsync(sessionId, cancellationToken);
        if (session is null || session.OwnerId != ownerId)
        {
            throw new NotFoundException("Session not found.");
        }

        return session;
    }
}
