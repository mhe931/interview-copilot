using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using InterviewCopilot.Domain.Entities;

namespace InterviewCopilot.Application.Copilot;

public sealed record PreparedAnswer(string FileName, string Text);

/// <summary>Indexes explicitly labelled question-and-answer pairs without sending them to a provider.</summary>
public sealed class PreparedAnswerIndex
{
    private const int MinimumAnswerLength = 20;
    private const int MaximumAnswerLength = 3_000;
    private static readonly Regex AnswerLabel = new(
        @"^(?:[\p{L}]+\s+){0,3}(?:answer|response)\s*:\s*(.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private readonly ConcurrentDictionary<Guid, IndexEntry> _sessions = new();

    public void Prepare(Guid sessionId, IReadOnlyList<SessionDocument> documents) =>
        _sessions[sessionId] = Build(documents);

    public PreparedAnswer? Find(Guid sessionId, IReadOnlyList<SessionDocument> documents, string question)
    {
        var ids = SortedDocumentIds(documents);
        var entry = _sessions.AddOrUpdate(
            sessionId,
            _ => Build(documents),
            (_, existing) => existing.DocumentIds.SequenceEqual(ids) ? existing : Build(documents));
        return entry.Answers.GetValueOrDefault(Normalize(question));
    }

    public void Remove(Guid sessionId) => _sessions.TryRemove(sessionId, out _);

    private static IndexEntry Build(IReadOnlyList<SessionDocument> documents)
    {
        var answers = new Dictionary<string, PreparedAnswer?>(StringComparer.Ordinal);
        foreach (var document in documents)
        {
            var lines = document.Content.Split('\n');
            for (var i = 1; i < lines.Length; i++)
            {
                var match = AnswerLabel.Match(lines[i].Trim());
                var question = lines[i - 1].Trim();
                if (!match.Success || !question.EndsWith('?'))
                {
                    continue;
                }

                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(match.Groups[1].Value))
                {
                    parts.Add(match.Groups[1].Value.Trim());
                }

                for (var j = i + 1; j < lines.Length; j++)
                {
                    var line = lines[j].Trim();
                    if (line.Length == 0 || line.EndsWith('?') || AnswerLabel.IsMatch(line))
                    {
                        break;
                    }

                    parts.Add(line);
                }

                var text = string.Join('\n', parts);
                var key = Normalize(question);
                if (key.Length == 0 || text.Length is < MinimumAnswerLength or > MaximumAnswerLength)
                {
                    continue;
                }

                var answer = new PreparedAnswer(document.FileName, text);
                if (answers.TryGetValue(key, out var previous))
                {
                    if (previous is null || !string.Equals(previous.Text, text, StringComparison.Ordinal))
                    {
                        answers[key] = null;
                    }
                }
                else
                {
                    answers.Add(key, answer);
                }
            }
        }

        return new IndexEntry(SortedDocumentIds(documents), answers);
    }

    private static Guid[] SortedDocumentIds(IReadOnlyList<SessionDocument> documents) =>
        [.. documents.Select(document => document.Id).Order()];

    private static string Normalize(string question)
    {
        var builder = new StringBuilder(question.Length);
        var pendingSpace = false;
        foreach (var character in question)
        {
            if (char.IsLetterOrDigit(character))
            {
                if (pendingSpace && builder.Length > 0) builder.Append(' ');
                builder.Append(char.ToLowerInvariant(character));
                pendingSpace = false;
            }
            else
            {
                pendingSpace = true;
            }
        }

        return builder.ToString();
    }

    private sealed record IndexEntry(Guid[] DocumentIds, Dictionary<string, PreparedAnswer?> Answers);
}
