namespace InterviewCopilot.Benchmark;

internal sealed record BenchmarkOptions(
    string BaseUrl,
    string QuestionsPath,
    string OutputPath,
    string Provider,
    string Model,
    string? ApiKeyEnvironmentVariable)
{
#pragma warning disable S1075 // The backend's fixed local address (GOTCHA-PROJECT.md), overridable with --base-url.
    public const string DefaultBaseUrl = "http://127.0.0.1:5480";
#pragma warning restore S1075
    public const string DefaultModel = "llama3.2:3b";
    public const string DefaultProvider = "ollama";
    private static readonly string[] SupportedProviders = ["ollama", "google", "anthropic", "openai"];

    /// <summary>Parses <c>--name value</c> or <c>--name=value</c>. Throws ArgumentException on anything else.</summary>
    public static BenchmarkOptions Parse(IReadOnlyList<string> args, string baseDirectory)
    {
        var baseUrl = DefaultBaseUrl;
        var questions = Path.Combine(baseDirectory, "questions.json");
        var output = Path.Combine("docs", "benchmarks", "latency.md");
        var provider = DefaultProvider;
        var model = DefaultModel;
        string? apiKeyEnvironmentVariable = null;

        var next = 0;
        while (next < args.Count)
        {
            var arg = args[next++];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unexpected argument '{arg}'.");
            }

            string name;
            string value;
            var equals = arg.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0)
            {
                name = arg[2..equals];
                value = arg[(equals + 1)..];
            }
            else
            {
                name = arg[2..];
                if (next >= args.Count)
                {
                    throw new ArgumentException($"Missing value for --{name}.");
                }

                value = args[next++];
            }

            switch (name)
            {
                case "base-url":
                    baseUrl = value;
                    break;
                case "questions":
                    questions = value;
                    break;
                case "output":
                    output = value;
                    break;
                case "provider":
                    provider = value;
                    break;
                case "model":
                    model = value;
                    break;
                case "api-key-env":
                    apiKeyEnvironmentVariable = value;
                    break;
                default:
                    throw new ArgumentException($"Unknown option --{name}. Options: --base-url, --questions, --output, --provider, --model, --api-key-env.");
            }
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException($"--base-url must be an http(s) URL, got '{baseUrl}'.");
        }

        var canonicalProvider = SupportedProviders.FirstOrDefault(
            supported => supported.Equals(provider, StringComparison.OrdinalIgnoreCase));
        if (canonicalProvider is null)
        {
            throw new ArgumentException($"Unsupported --provider '{provider}'. Choose: {string.Join(", ", SupportedProviders)}.");
        }

        if (canonicalProvider == DefaultProvider && apiKeyEnvironmentVariable is not null)
        {
            throw new ArgumentException("--api-key-env is only valid for a cloud provider.");
        }

        if (canonicalProvider != DefaultProvider && apiKeyEnvironmentVariable is null)
        {
            throw new ArgumentException($"--api-key-env is required for cloud provider '{canonicalProvider}'.");
        }

        if (apiKeyEnvironmentVariable is not null && string.IsNullOrWhiteSpace(apiKeyEnvironmentVariable))
        {
            throw new ArgumentException("--api-key-env must name a non-empty environment variable.");
        }

        return new BenchmarkOptions(baseUrl, questions, output, canonicalProvider, model, apiKeyEnvironmentVariable);
    }
}
