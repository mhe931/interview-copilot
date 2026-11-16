namespace InterviewCopilot.Application.Copilot;

public sealed class AnswerTimeoutOptions
{
    public TimeSpan FirstToken { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan IdleStream { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan Total { get; init; } = TimeSpan.FromSeconds(60);
}

public enum AnswerTimeoutPhase
{
    FirstToken,
    IdleStream,
    Total,
}

public sealed class AnswerStreamTimeoutException(AnswerTimeoutPhase phase)
    : TimeoutException(MessageFor(phase))
{
    public AnswerTimeoutPhase Phase { get; } = phase;

    private static string MessageFor(AnswerTimeoutPhase phase) => phase switch
    {
        AnswerTimeoutPhase.FirstToken =>
            "The model did not start responding within the first-token limit. Check provider connectivity, model access, and service load.",
        AnswerTimeoutPhase.IdleStream =>
            "Answer streaming stalled after partial text. The text received so far is preserved; check the provider connection and retry if needed.",
        _ =>
            "Answer generation exceeded its total time limit. The text received so far is preserved; shorten the answer or reduce grounded context, then retry.",
    };
}
