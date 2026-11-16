using System.Net.WebSockets;
using System.Security.Claims;
using InterviewCopilot.Api.Infrastructure;
using InterviewCopilot.Application.Audio;

namespace InterviewCopilot.Api.Endpoints;

internal static partial class AudioEndpoints
{
    public const string AudioPathPrefix = "/ws/";
    private static readonly TimeSpan CloseHandshakeTimeout = TimeSpan.FromSeconds(1);

    public static void MapAudioEndpoints(this IEndpointRouteBuilder app)
    {
        // Receives [speaker][PCM16] frames from the desktop capture window (docs/atlas-gotcha/audio-capture-windows.md).
        app.Map("/ws/sessions/{sessionId:guid}/audio", async (
                Guid sessionId,
                HttpContext context,
                ClaimsPrincipal user,
                AudioIngestService ingest,
                ILoggerFactory loggerFactory,
                CancellationToken ct) =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    return Results.BadRequest("Expected a WebSocket request.");
                }

                using var stream = await ingest.OpenStreamAsync(user.UserId(), sessionId, ct); // 404 / 409 before the upgrade
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                using var receiving = CancellationTokenSource.CreateLinkedTokenSource(ct);
                using var closeGate = new SemaphoreSlim(1, 1);
                var malformed = false;
                var receive = ReceiveAsync(socket, sessionId, ingest, closeGate, receiving.Token);
                try
                {
                    var stopping = Task.Delay(Timeout.InfiniteTimeSpan, stream.Stopping);
                    if (await Task.WhenAny(receive, stopping) == stopping && !receive.IsCompleted)
                    {
                        receiving.CancelAfter(CloseHandshakeTimeout);
                        await CloseOutputAsync(socket, closeGate, receiving.Token);
                    }

                    malformed = await receive;
                }
                catch (OperationCanceledException) when (stream.Stopping.IsCancellationRequested)
                {
                    LogAudioDrainTimeout(loggerFactory.CreateLogger(nameof(AudioEndpoints)), sessionId);
                    socket.Abort();
                    try
                    {
                        await receive;
                    }
                    catch (OperationCanceledException) when (receiving.IsCancellationRequested)
                    {
                        // The timeout cancelled the receive.
                    }
                    catch (WebSocketException)
                    {
                        // Aborting the socket also ends an outstanding receive.
                    }
                }
                finally
                {
                    // Whatever ended the stream (close, bad frame, dropped client, shutdown), flush what was said.
                    await ingest.EndStreamAsync(sessionId, CancellationToken.None);
                }

                stream.Dispose();
                if (malformed)
                {
                    // After the flush: this waits for the client's close reply, which a bad client may never send.
                    await socket.CloseAsync(WebSocketCloseStatus.InvalidMessageType, "Malformed audio frame.", ct);
                }

                return Results.Empty;
            })
            .WithTags("Audio")
            .RequireAuthorization();

        app.MapGet("/api/sessions/{sessionId:guid}/audio/stats", async (
                Guid sessionId,
                ClaimsPrincipal user,
                Application.Sessions.SessionService sessions,
                AudioStatsSink stats,
                CancellationToken ct) =>
            {
                await sessions.GetAsync(user.UserId(), sessionId, ct); // ownership check
                return stats.Get(sessionId);
            })
            .WithTags("Audio")
            .RequireAuthorization();
    }

    /// <summary>Reads frames until the stream ends. Returns true when it ended on a malformed frame the caller must close for.</summary>
    private static async Task<bool> ReceiveAsync(WebSocket socket, Guid sessionId, AudioIngestService ingest, SemaphoreSlim closeGate, CancellationToken ct)
    {
        // One byte more than the limit, so an oversized message is detectable.
        var buffer = new byte[AudioFrameParser.MaxMessageBytes + 1];
        while (socket.State is WebSocketState.Open or WebSocketState.CloseSent && !ct.IsCancellationRequested)
        {
            var length = 0;
            ValueWebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer.AsMemory(length), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await CloseOutputAsync(socket, closeGate, ct);
                    return false;
                }

                length += result.Count;
            }
            while (!result.EndOfMessage && length < buffer.Length);

            if (result.MessageType != WebSocketMessageType.Binary
                || !result.EndOfMessage
                || !AudioFrameParser.TryParse(buffer.AsMemory(0, length), out var frame))
            {
                return true;
            }

            // Copy: the receive buffer is reused for the next message.
            await ingest.IngestAsync(sessionId, frame with { Pcm16 = frame.Pcm16.ToArray() }, ct);
        }

        ct.ThrowIfCancellationRequested();
        return false;
    }

    private static async Task CloseOutputAsync(WebSocket socket, SemaphoreSlim closeGate, CancellationToken ct)
    {
        await closeGate.WaitAsync(ct);
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct);
            }
        }
        finally
        {
            closeGate.Release();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Audio stream for session {SessionId} did not close in time.")]
    private static partial void LogAudioDrainTimeout(ILogger logger, Guid sessionId);
}
