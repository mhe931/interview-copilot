using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using InterviewCopilot.Application.Common.Exceptions;
using InterviewCopilot.Application.Copilot;
using InterviewCopilot.Application.Events;
using InterviewCopilot.Application.Providers;
using InterviewCopilot.Domain.Entities;
using InterviewCopilot.Domain.Enums;
using InterviewCopilot.UnitTests.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InterviewCopilot.UnitTests.Copilot;

public class AnswerGeneratorTests
{
    private readonly InMemoryStore _store = new();
    private readonly Session _session = Session.Create(Guid.NewGuid(), "Interview", SessionMode.LiveInterview, FixedClock.Default);

    public AnswerGeneratorTests() => _store.Add(_session);

    private readonly RecordingPublisher _events = new();

    private readonly InMemoryPreferences _preferences = new();

    private readonly InMemorySessionKeyStore _keys = new();
    private readonly PreparedAnswerIndex _prepared = new();

    private AnswerGenerator Generator(params ILlmProvider[] llms) =>
        new(_store, _store, _store, new LlmProviderResolver(llms), _keys,
            Options.Create(new LlmOptions { Model = "llama3.2:3b", Temperature = 0.2 }), new FixedClock(), _events, _preferences,
            NullLogger<AnswerGenerator>.Instance, _prepared);

    private AnswerGenerator GeneratorWithTimeouts(AnswerTimeoutOptions timeouts, params ILlmProvider[] llms) =>
        new(_store, _store, _store, new LlmProviderResolver(llms), _keys,
            Options.Create(new LlmOptions { Model = "llama3.2:3b", Temperature = 0.2 }), new FixedClock(), _events, _preferences,
            NullLogger<AnswerGenerator>.Instance, _prepared, timeouts);

    [Theory]
    [InlineData("qwen2.5:7b", "qwen2.5:7b")]
    [InlineData(null, "llama3.2:3b")]
    public async Task Should_UseOwnersPreferredModel_Else_ConfiguredDefault(string? preferred, string expected)
    {
        var preferences = UserPreferences.Create(_session.OwnerId, FixedClock.Default);
        preferences.SetLlmModel(preferred, FixedClock.Default);
        _preferences.Add(preferences);
        var llm = new FakeLlm("x");

        var (_, tokens) = await Generator(llm).GenerateAsync(new AnswerRequest(_session.Id, "Why this company?"), TestContext.Current.CancellationToken);
        await foreach (var token in tokens)
        {
            _ = token;
        }

        llm.LastRequest!.Model.Should().Be(expected);
    }

    [Fact]
    public async Task Should_UseConfiguredDefault_When_OwnerHasNoPreferences()
    {
        var llm = new FakeLlm("x");

        var (_, tokens) = await Generator(llm).GenerateAsync(new AnswerRequest(_session.Id, "Why this company?"), TestContext.Current.CancellationToken);
        await foreach (var token in tokens)
        {
            _ = token;
        }

        llm.LastRequest!.Model.Should().Be("llama3.2:3b");
    }

    [Fact]
    public async Task Should_PublishDeltaPerToken_And_DoneAfterPersisting()
    {
        var (_, tokens) = await Generator(new FakeLlm("I ", "led ", "it.")).GenerateAsync(
            new AnswerRequest(_session.Id, "Tell me about a time you led a team"),
            TestContext.Current.CancellationToken);

        await foreach (var token in tokens)
        {
            _events.Published.Should().NotContain(e => e.Type == SessionEventTypes.AnswerDone);
            _ = token;
        }

        _events.Published.Select(e => e.Type).Should().Equal(
            SessionEventTypes.AnswerDelta,
            SessionEventTypes.AnswerDelta,
            SessionEventTypes.AnswerDelta,
            SessionEventTypes.AnswerDone);
        _events.Published.Should().OnlyContain(e => e.SessionId == _session.Id);
        var json = _events.Published.Select(e => JsonSerializer.Serialize(e.Data, Web)).ToList();
        json[0].Should().Be("{\"token\":\"I \",\"version\":1,\"questionType\":\"Behavioral\",\"origin\":\"Generated\",\"sourceDocument\":null,\"segmentId\":null,\"requestId\":null}");
        json[3].Should().Be($"{{\"version\":1,\"questionType\":\"Behavioral\",\"origin\":\"Generated\",\"sourceDocument\":null,\"answerId\":\"{_store.Answers.Single().Id}\",\"segmentId\":null,\"requestId\":null}}");
    }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private sealed class RecordingPublisher : ISessionEventPublisher
    {
        public List<(Guid SessionId, string Type, object Data)> Published { get; } = [];

        public void Publish(Guid sessionId, string type, object data) => Published.Add((sessionId, type, data));
    }

    [Fact]
    public async Task Should_StreamTokens_And_PersistAnswer()
    {
        var llm = new FakeLlm("I ", "led ", "it.");
        var (header, tokens) = await Generator(llm).GenerateAsync(
            new AnswerRequest(_session.Id, "Tell me about a time you led a team", ApiKey: "byok"),
            TestContext.Current.CancellationToken);

        var streamed = new List<string>();
        await foreach (var token in tokens)
        {
            streamed.Add(token);
        }

        header.QuestionType.Should().Be(QuestionType.Behavioral);
        header.Version.Should().Be(1);
        string.Concat(streamed).Should().Be("I led it.");
        _store.Answers.Should().ContainSingle().Which.Text.Should().Be("I led it.");
        _store.SaveCount.Should().Be(1);
        llm.LastRequest!.Model.Should().Be("llama3.2:3b");
        llm.LastRequest.ApiKey.Should().Be("byok");
    }

    [Fact]
    public async Task Should_IncrementVersion_When_Regenerating()
    {
        var (header, _) = await Generator(new FakeLlm("x")).GenerateAsync(
            new AnswerRequest(_session.Id, "Why this company?", AnswerModifier.Regenerate, "old", PreviousVersion: 2),
            TestContext.Current.CancellationToken);

        header.Version.Should().Be(3);
    }

    [Fact]
    public async Task Should_StreamPreparedAnswerWithoutCallingProvider_When_ExactQuestionMatches()
    {
        _session.AttachDocument("prepared.txt", """
            What is your research method?
            Suggested short answer:
            I combined interviews with quantitative analysis.
            """, FixedClock.Default);
        var llm = new FakeLlm("generated");
        var (header, tokens) = await Generator(llm).GenerateAsync(
            new AnswerRequest(_session.Id, "What is your research method?"), TestContext.Current.CancellationToken);

        var streamed = new List<string>();
        await foreach (var token in tokens) streamed.Add(token);

        string.Concat(streamed).Should().Be("I combined interviews with quantitative analysis.");
        llm.LastRequest.Should().BeNull();
        header.Origin.Should().Be(AnswerOrigin.Prepared);
        header.SourceDocument.Should().Be("prepared.txt");
        _store.Answers.Single().Origin.Should().Be(AnswerOrigin.Prepared);
        _store.Answers.Single().SourceDocument.Should().Be("prepared.txt");
    }

    [Fact]
    public async Task Should_Generate_When_PreparedAnswerIsModified()
    {
        _session.AttachDocument("prepared.txt", """
            What is your research method?
            Answer:
            I combined interviews with quantitative analysis.
            """, FixedClock.Default);
        var llm = new FakeLlm("longer");
        var (header, tokens) = await Generator(llm).GenerateAsync(
            new AnswerRequest(_session.Id, "What is your research method?", AnswerModifier.Longer, "earlier"),
            TestContext.Current.CancellationToken);

        await foreach (var token in tokens) _ = token;

        llm.LastRequest.Should().NotBeNull();
        header.Origin.Should().Be(AnswerOrigin.Generated);
    }

    [Fact]
    public async Task Should_Throw_When_SessionMissing()
    {
        var act = () => Generator(new FakeLlm()).GenerateAsync(new AnswerRequest(Guid.NewGuid(), "Q?"), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Should_Timeout_When_FirstTokenDoesNotArrive()
    {
        var generator = GeneratorWithTimeouts(
            new AnswerTimeoutOptions { FirstToken = TimeSpan.FromMilliseconds(20), IdleStream = TimeSpan.FromSeconds(1), Total = TimeSpan.FromSeconds(1) },
            new BlockingLlm());
        var (_, tokens) = await generator.GenerateAsync(
            new AnswerRequest(_session.Id, "Why this company?"),
            TestContext.Current.CancellationToken);
        var act = async () =>
        {
            await foreach (var token in tokens)
            {
                _ = token;
            }
        };

        await act.Should().ThrowAsync<AnswerStreamTimeoutException>()
            .Where(ex => ex.Phase == AnswerTimeoutPhase.FirstToken);
    }

    [Fact]
    public async Task Should_TimeoutIdleStream_AndKeepPartialDeltaWithoutPersisting()
    {
        var generator = GeneratorWithTimeouts(
            new AnswerTimeoutOptions { FirstToken = TimeSpan.FromSeconds(1), IdleStream = TimeSpan.FromMilliseconds(20), Total = TimeSpan.FromSeconds(1) },
            new BlockingLlm("partial"));
        var (_, tokens) = await generator.GenerateAsync(
            new AnswerRequest(_session.Id, "Why this company?"),
            TestContext.Current.CancellationToken);
        var received = new List<string>();
        var act = async () =>
        {
            await foreach (var token in tokens)
            {
                received.Add(token);
            }
        };

        await act.Should().ThrowAsync<AnswerStreamTimeoutException>()
            .Where(ex => ex.Phase == AnswerTimeoutPhase.IdleStream);
        received.Should().Equal("partial");
        _events.Published.Select(e => e.Type).Should().Equal(SessionEventTypes.AnswerDelta);
        _store.Answers.Should().BeEmpty();
    }

    [Fact]
    public async Task Should_EnforceTotalDeadline_When_ItIsShorterThanPhaseDeadlines()
    {
        var generator = GeneratorWithTimeouts(
            new AnswerTimeoutOptions { FirstToken = TimeSpan.FromSeconds(1), IdleStream = TimeSpan.FromSeconds(1), Total = TimeSpan.FromMilliseconds(20) },
            new BlockingLlm());
        var (_, tokens) = await generator.GenerateAsync(
            new AnswerRequest(_session.Id, "Why this company?"),
            TestContext.Current.CancellationToken);
        var act = async () =>
        {
            await foreach (var token in tokens)
            {
                _ = token;
            }
        };

        await act.Should().ThrowAsync<AnswerStreamTimeoutException>()
            .Where(ex => ex.Phase == AnswerTimeoutPhase.Total);
    }

    private sealed class BlockingLlm(string? firstToken = null) : ILlmProvider
    {
        public string Name => ProviderNames.Ollama;

        public async IAsyncEnumerable<string> StreamAsync(
            LlmRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (firstToken is not null)
            {
                yield return firstToken;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }
}
