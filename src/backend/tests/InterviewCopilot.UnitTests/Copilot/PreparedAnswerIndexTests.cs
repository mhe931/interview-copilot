using FluentAssertions;
using InterviewCopilot.Application.Copilot;
using InterviewCopilot.Domain.Entities;
using InterviewCopilot.Domain.Enums;

namespace InterviewCopilot.UnitTests.Copilot;

public sealed class PreparedAnswerIndexTests
{
    private readonly PreparedAnswerIndex _index = new();
    private readonly Session _session = Session.Create(Guid.NewGuid(), "Test", SessionMode.LiveInterview, FixedClock.Default);

    [Fact]
    public void Should_ReuseOnlyExactLabeledQuestionAndAnswer()
    {
        _session.AttachDocument("questions.txt", """
            What is your research method?
            1
            2
            Question 1
            What is your research method?
            Suggested short answer:
            I used a mixed-method design to compare the results.

            Question 2
            Why did you choose this method?
            Suggested short answer:
            It combines quantitative evidence with interview context.
            """, FixedClock.Default);
        _index.Prepare(_session.Id, _session.Documents);

        var match = _index.Find(_session.Id, _session.Documents, "what is your research method");

        match.Should().NotBeNull();
        match.Text.Should().Be("I used a mixed-method design to compare the results.");
        match.FileName.Should().Be("questions.txt");
        _index.Find(_session.Id, _session.Documents, "What is not your research method?").Should().BeNull();
    }

    [Fact]
    public void Should_RejectAmbiguousAnswersAndUnlabeledText()
    {
        _session.AttachDocument("a.txt", """
            Why is the method suitable?
            Answer:
            It is suitable because it compares independent results.

            Why is the sample small?
            An unlabeled paragraph must not be used as an answer.
            """, FixedClock.Default);
        _session.AttachDocument("b.txt", """
            Why is the method suitable?
            Answer:
            It is suitable for a different and incompatible reason.
            """, FixedClock.Default);

        _index.Find(_session.Id, _session.Documents, "Why is the method suitable?").Should().BeNull();
        _index.Find(_session.Id, _session.Documents, "Why is the sample small?").Should().BeNull();
    }

    [Fact]
    public void Should_InvalidateWhenDocumentsChange()
    {
        var document = _session.AttachDocument("a.txt", """
            What is your method?
            Answer:
            I compare results from two independent sources.
            """, FixedClock.Default);
        _index.Prepare(_session.Id, _session.Documents);
        _index.Find(_session.Id, _session.Documents, "What is your method?").Should().NotBeNull();

        _session.RemoveDocument(document.Id);
        _index.Find(_session.Id, _session.Documents, "What is your method?").Should().BeNull();
        _session.AttachDocument("b.txt", """
            What is your method?
            Answer:
            I use interviews to examine the practical outcomes.
            """, FixedClock.Default);
        _index.Find(_session.Id, _session.Documents, "What is your method?")!.Text
            .Should().Be("I use interviews to examine the practical outcomes.");
    }
}
