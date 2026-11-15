using FluentAssertions;
using InterviewCopilot.Application.Common.Exceptions;
using InterviewCopilot.Application.Sessions;

namespace InterviewCopilot.UnitTests.Services;

public sealed class SessionRuntimeTests
{
    [Fact]
    public async Task Should_RejectOverlappingStream_And_WaitUntilOldStreamIsDrained()
    {
        using var runtime = new SessionRuntime();
        var id = Guid.NewGuid();
        var first = runtime.OpenStream(id, 500);
        ((Action)(() => runtime.OpenStream(id, 600))).Should().Throw<ConflictException>();

        var stop = runtime.StopStreamAsync(id);
        first.Stopping.IsCancellationRequested.Should().BeTrue();
        stop.IsCompleted.Should().BeFalse();
        first.Dispose();
        await stop;

        using var second = runtime.OpenStream(id, 1_000);
        runtime.OriginMs(id).Should().Be(1_000);
        first.Dispose();
        runtime.OriginMs(id).Should().Be(1_000, "late disposal cannot clear the new stream");
    }
}
