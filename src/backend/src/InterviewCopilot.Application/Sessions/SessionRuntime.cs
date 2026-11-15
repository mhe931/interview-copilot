using System.Collections.Concurrent;
using InterviewCopilot.Application.Common.Exceptions;

namespace InterviewCopilot.Application.Sessions;

/// <summary>Serializes lifecycle transitions and owns exactly one audio connection per session.</summary>
public sealed class SessionRuntime : IDisposable
{
    private readonly ConcurrentDictionary<Guid, State> _sessions = new();

    public async Task<IDisposable> EnterAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var state = _sessions.GetOrAdd(sessionId, _ => new State());
        await state.Transition.WaitAsync(cancellationToken);
        return new TransitionLease(state.Transition);
    }

    public AudioStreamLease OpenStream(Guid sessionId, long originMs)
    {
        var state = _sessions.GetOrAdd(sessionId, _ => new State());
        lock (state.Gate)
        {
            if (state.Stream is not null)
            {
                throw new ConflictException("Audio is already streaming for this session. Stop the current capture first.");
            }

            var stream = new AudioStreamLease(originMs, () =>
            {
                lock (state.Gate)
                {
                    state.Stream = null;
                }
            });
            state.Stream = stream;
            return stream;
        }
    }

    public long OriginMs(Guid sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var state))
        {
            return 0;
        }

        lock (state.Gate)
        {
            return state.Stream?.OriginMs ?? 0;
        }
    }

    public async Task StopStreamAsync(Guid sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var state))
        {
            return;
        }

        Task completed;
        lock (state.Gate)
        {
            if (state.Stream is not { } stream)
            {
                return;
            }

            stream.Stop();
            completed = stream.Completed;
        }

        await completed;
    }

    public void Dispose()
    {
        foreach (var state in _sessions.Values)
        {
            state.Stream?.Stop();
            state.Transition.Dispose();
        }
    }

    private sealed class State
    {
        public Lock Gate { get; } = new();
        public SemaphoreSlim Transition { get; } = new(1, 1);
        public AudioStreamLease? Stream { get; set; }
    }

    private sealed class TransitionLease(SemaphoreSlim semaphore) : IDisposable
    {
        public void Dispose() => semaphore.Release();
    }
}

public sealed class AudioStreamLease(long originMs, Action release) : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    public long OriginMs { get; } = originMs;
    public CancellationToken Stopping => _stop.Token;
    public Task Completed => _completed.Task;

    internal void Stop() => _stop.Cancel();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        release();
        _completed.TrySetResult();
        _stop.Dispose();
    }
}
