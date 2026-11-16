using FluentAssertions;
using InterviewCopilot.Benchmark;

namespace InterviewCopilot.UnitTests.Benchmark;

public sealed class BenchmarkOptionsTests
{
    [Fact]
    public void Parse_ShouldUseDefaults_WhenNoArguments()
    {
        var options = BenchmarkOptions.Parse([], "/app");

        options.BaseUrl.Should().Be("http://127.0.0.1:5480");
        options.QuestionsPath.Should().Be(Path.Combine("/app", "questions.json"));
        options.OutputPath.Should().Be(Path.Combine("docs", "benchmarks", "latency.md"));
        options.Provider.Should().Be("ollama");
        options.Model.Should().Be("llama3.2:3b");
        options.ApiKeyEnvironmentVariable.Should().BeNull();
    }

    [Fact]
    public void Parse_ShouldReadSeparatedAndEqualsForms()
    {
        var options = BenchmarkOptions.Parse(
            ["--base-url", "http://localhost:9999", "--questions=q.json", "--output", "out.md", "--provider=google", "--model=gemini-3.5-flash-lite", "--api-key-env", "GOOGLE_API_KEY"],
            "/app");

        options.BaseUrl.Should().Be("http://localhost:9999");
        options.QuestionsPath.Should().Be("q.json");
        options.OutputPath.Should().Be("out.md");
        options.Provider.Should().Be("google");
        options.Model.Should().Be("gemini-3.5-flash-lite");
        options.ApiKeyEnvironmentVariable.Should().Be("GOOGLE_API_KEY");
    }

    [Theory]
    [InlineData("--unknown", "x")]
    [InlineData("--model")]
    [InlineData("positional")]
    [InlineData("--provider", "google")]
    [InlineData("--provider", "ollama", "--api-key-env", "GOOGLE_API_KEY")]
    [InlineData("--api-key-env", " ")]
    public void Parse_ShouldRejectInvalidArguments(params string[] args)
    {
        var act = () => BenchmarkOptions.Parse(args, "/app");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Parse_ShouldRejectInvalidBaseUrl()
    {
        var act = () => BenchmarkOptions.Parse(["--base-url", "not a url"], "/app");

        act.Should().Throw<ArgumentException>().WithMessage("*base-url*");
    }
}
