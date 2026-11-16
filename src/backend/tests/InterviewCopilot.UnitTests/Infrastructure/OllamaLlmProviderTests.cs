using System.Net;
using FluentAssertions;
using InterviewCopilot.Application.Providers;
using InterviewCopilot.Infrastructure.Llm;
using Microsoft.Extensions.Logging;

namespace InterviewCopilot.UnitTests.Infrastructure;

public class OllamaLlmProviderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_LogProviderResponseTimingWithoutLoggingPromptText()
    {
        var logger = new CapturingLogger();
        var handler = new CloudFakeHandler(
            HttpStatusCode.OK,
            "{\"message\":{\"role\":\"assistant\",\"content\":\"Hi\"},\"done\":false}\n{\"done\":true}\n",
            "application/x-ndjson");
        var provider = new OllamaLlmProvider(new HttpClient(handler) { BaseAddress = new Uri("http://ollama.test/") }, logger);
        var request = new LlmRequest(
            "local-model",
            [new LlmMessage(LlmRole.User, "private prompt")],
            0.1,
            RequestId: Guid.NewGuid());

        var tokens = await provider.StreamAsync(request, Ct).ToListAsync(Ct);

        tokens.Should().Equal("Hi");
        logger.Messages.Should().ContainSingle(message =>
            message.Contains("Ollama response headers", StringComparison.Ordinal)
            && message.Contains("local-model", StringComparison.Ordinal)
            && message.Contains("ms", StringComparison.Ordinal));
        logger.Messages.Should().NotContain(message => message.Contains("private prompt", StringComparison.Ordinal));
    }

    private sealed class CapturingLogger : ILogger<OllamaLlmProvider>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }
}
