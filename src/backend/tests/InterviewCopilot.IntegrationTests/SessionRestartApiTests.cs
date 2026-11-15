using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using InterviewCopilot.Application.Sessions;
using InterviewCopilot.Domain.Entities;
using InterviewCopilot.Domain.Enums;
using InterviewCopilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace InterviewCopilot.IntegrationTests;

public sealed class SessionRestartApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { Converters = { new JsonStringEnumConverter() } };
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<SessionDto> CreateAsync(HttpClient client, Guid? source = null)
    {
        var response = await client.PostAsJsonAsync("/api/sessions", new
        {
            Title = source is null ? "Original" : "Editable duplicate",
            Mode = "Conversation", AnswerLength = "Short", Framework = "Star",
            PinnedQuestionType = "Behavioral", SessionPrompt = "Reusable context", CopyFromSessionId = source,
        }, Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SessionDto>(Json, Ct))!;
    }

    [Fact]
    public async Task Should_DuplicateConfigurationAndDocuments_WithoutHistoryOrRuntime()
    {
        var client = await factory.CreateSignedInClientAsync("duplicate-ended");
        var original = await CreateAsync(client);
        (await client.PutAsJsonAsync($"/api/sessions/{original.Id}/auto-answer", new { Enabled = false }, Ct)).EnsureSuccessStatusCode();
        using var document = new MultipartFormDataContent();
        document.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("Reusable document")), "file", "context.txt");
        (await client.PostAsync($"/api/sessions/{original.Id}/documents", document, Ct)).EnsureSuccessStatusCode();
        (await client.PostAsync($"/api/sessions/{original.Id}/start", null, Ct)).EnsureSuccessStatusCode();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InterviewCopilotDbContext>();
            db.TranscriptSegments.Add(TranscriptSegment.Create(original.Id, Speaker.Interviewer, 0, 500, "Earlier question?"));
            db.SuggestedAnswers.Add(SuggestedAnswer.Create(original.Id, "Earlier question?", QuestionType.Behavioral,
                "Earlier answer", 1, AnswerModifier.None, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(Ct);
        }

        (await client.PostAsync($"/api/sessions/{original.Id}/end", null, Ct)).EnsureSuccessStatusCode();
        var copy = await CreateAsync(client, original.Id);
        copy.Id.Should().NotBe(original.Id);
        copy.Title.Should().Be("Editable duplicate");
        copy.Mode.Should().Be(original.Mode);
        copy.AnswerLength.Should().Be(original.AnswerLength);
        copy.Framework.Should().Be(original.Framework);
        copy.PinnedQuestionType.Should().Be(original.PinnedQuestionType);
        copy.SessionPrompt.Should().Be(original.SessionPrompt);
        copy.AutoAnswer.Should().BeFalse();
        copy.Status.Should().Be(SessionStatus.Draft);
        copy.StartedAt.Should().BeNull();
        copy.EndedAt.Should().BeNull();
        copy.AccumulatedDurationMs.Should().Be(0);
        await using var verification = factory.Services.CreateAsyncScope();
        var persisted = verification.ServiceProvider.GetRequiredService<InterviewCopilotDbContext>();
        (await persisted.TranscriptSegments.CountAsync(t => t.SessionId == original.Id, Ct)).Should().Be(1);
        (await persisted.SuggestedAnswers.CountAsync(t => t.SessionId == original.Id, Ct)).Should().Be(1);
        (await persisted.TranscriptSegments.CountAsync(t => t.SessionId == copy.Id, Ct)).Should().Be(0);
        (await persisted.SuggestedAnswers.CountAsync(t => t.SessionId == copy.Id, Ct)).Should().Be(0);
        var sourceDocument = await persisted.SessionDocuments.SingleAsync(d => d.SessionId == original.Id, Ct);
        var copiedDocument = await persisted.SessionDocuments.SingleAsync(d => d.SessionId == copy.Id, Ct);
        copiedDocument.Id.Should().NotBe(sourceDocument.Id);
        copiedDocument.Content.Should().Be(sourceDocument.Content);
        copiedDocument.FileName.Should().Be(sourceDocument.FileName);
        (await persisted.Sessions.SingleAsync(s => s.Id == original.Id, Ct)).Status.Should().Be(SessionStatus.Ended);
        (await client.PostAsync($"/api/sessions/{copy.Id}/start", null, Ct)).EnsureSuccessStatusCode();
        (await client.PostAsync($"/api/sessions/{copy.Id}/end", null, Ct)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Should_InitializeLegacyDuration_And_PersistAfterRestart()
    {
        var client = await factory.CreateSignedInClientAsync("legacy-restart");
        var created = await CreateAsync(client);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InterviewCopilotDbContext>();
            var session = await db.Sessions.SingleAsync(s => s.Id == created.Id, Ct);
            var start = DateTimeOffset.UtcNow.AddHours(-2);
            session.Start(start);
            session.End(start.AddMinutes(2));
            session.SetAutoAnswer(false);
            await db.SaveChangesAsync(Ct);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE Sessions SET AccumulatedDurationMs = NULL WHERE Id = {created.Id}", Ct);
        }

        var legacy = await client.GetFromJsonAsync<SessionDto>($"/api/sessions/{created.Id}", Json, Ct);
        legacy!.AccumulatedDurationMs.Should().Be(120_000);
        var restarted = await (await client.PostAsync($"/api/sessions/{created.Id}/start", null, Ct))
            .Content.ReadFromJsonAsync<SessionDto>(Json, Ct);
        restarted!.Id.Should().Be(created.Id);
        restarted.EndedAt.Should().BeNull();
        restarted.AccumulatedDurationMs.Should().Be(120_000);
        restarted.AutoAnswer.Should().BeFalse();
        (await client.PostAsync($"/api/sessions/{created.Id}/end", null, Ct)).EnsureSuccessStatusCode();
        await using var verification = factory.Services.CreateAsyncScope();
        var reloaded = await verification.ServiceProvider.GetRequiredService<InterviewCopilotDbContext>()
            .Sessions.SingleAsync(s => s.Id == created.Id, Ct);
        reloaded.AccumulatedDurationMs.Should().BeInRange(120_000, 125_000);
        reloaded.Status.Should().Be(SessionStatus.Ended);
        reloaded.AutoAnswer.Should().BeFalse();
        var stranger = await factory.CreateSignedInClientAsync("foreign-restart");
        (await stranger.PostAsync($"/api/sessions/{created.Id}/start", null, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
