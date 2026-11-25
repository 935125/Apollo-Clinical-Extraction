using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Apollo.Backend.Clinical;
using Apollo.Backend.Emr;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace Apollo.Backend.Tests;

public class Stage13Top10QueueAndForensicsTests
{
    private static async Task<string> FetchCsrfAsync(HttpClient client)
    {
        var resp = await client.GetAsync("/api/v1/emr/auth/csrf");
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("token").GetString()!;
    }

    private static async Task LoginAsync(HttpClient client, string login, string password)
    {
        string csrf = await FetchCsrfAsync(client);
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/emr/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest(login, password))
        };
        req.Headers.Add("X-XSRF-TOKEN", csrf);
        (await client.SendAsync(req)).EnsureSuccessStatusCode();
    }

    [PostgresFact]
    public async Task QueueApi_ReturnsTop10IndividualSubmissionEvents_RetainsRepeatedPatientsAndFailedEvents_AndPaginates()
    {
        await using var fixture = new Stage9EmrFixture();
        await fixture.InitializeAsync();
        var (docA, passA) = await fixture.SeedDoctorAsync("top10.a@apollo.local", "Dr. Top10", "Doctor", "Top10Password123!");
        var enc1 = await fixture.SeedEncounterAsync(docA, "P930001", "ENC-930001");
        var enc2 = await fixture.SeedEncounterAsync(docA, "P930002", "ENC-930002");
        var enc3 = await fixture.SeedEncounterAsync(docA, "P930003", "ENC-930003");

        var broker = new SimulatedNoteBroker();
        using var factory = new Stage9EmrFactory { DatabaseConnection = fixture.ConnectionString, CustomBroker = broker, PrimaryProvider = "LocalOnnx" };
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        await LoginAsync(client, "top10.a@apollo.local", passA);

        // Create 12 signed extraction events across 3 patients (4 submissions per patient -> must NOT collapse to 3!)
        Guid[] encounters = [enc1, enc2, enc3];
        var noteByEncounter = new Dictionary<Guid, NoteDto>();
        Guid failedNoteId = Guid.Empty;
        int failedVersion = 0;

        for (int i = 1; i <= 12; i++)
        {
            var enc = encounters[(i - 1) % 3];
            string text = $"Chief Complaint: Synthetic Top10 submission #{i:D2}. Continue Amlodipine 5 mg 1 tablet orally once daily.";
            NoteDto draft;
            string csrf = await FetchCsrfAsync(client);
            if (!noteByEncounter.TryGetValue(enc, out var prev))
            {
                var create = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/emr/encounters/{enc}/notes")
                {
                    Content = JsonContent.Create(new DraftRequest(text))
                };
                create.Headers.Add("X-XSRF-TOKEN", csrf);
                draft = (await (await client.SendAsync(create)).Content.ReadFromJsonAsync<NoteDto>())!;
            }
            else
            {
                var corr = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/emr/notes/{prev.Id}/corrections")
                {
                    Content = JsonContent.Create(new DraftRequest(text, prev.Version))
                };
                corr.Headers.Add("X-XSRF-TOKEN", csrf);
                draft = (await (await client.SendAsync(corr)).Content.ReadFromJsonAsync<NoteDto>())!;
            }

            csrf = await FetchCsrfAsync(client);
            var sign = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/emr/notes/{draft.Id}/sign")
            {
                Content = JsonContent.Create(new SignRequest(draft.Version))
            };
            sign.Headers.Add("X-XSRF-TOKEN", csrf);
            var signed = (await (await client.SendAsync(sign)).Content.ReadFromJsonAsync<NoteDto>())!;
            noteByEncounter[enc] = signed;

            if (i == 9)
            {
                failedNoteId = signed.Id;
                failedVersion = signed.Version;
            }
        }

        // Mark submission #9 as Failed with AZURE_INVALID_EVIDENCE (simulating Case 4 durable failure state)
        await using (var conn = new NpgsqlConnection(fixture.ConnectionString))
        {
            await conn.OpenAsync();
            await EmrStore.Execute(conn,
                "UPDATE emr_outbox_events SET publication_status='Published', processing_status='Failed', processing_error='AZURE_INVALID_EVIDENCE' WHERE note_id=@nid AND note_version=@ver",
                default, null, ("nid", failedNoteId), ("ver", failedVersion));
        }

        // 1. Default GET /api/v1/queue returns EXACTLY 10 newest events out of 12 total
        var top10 = await client.GetFromJsonAsync<QueueListResponse>("/api/v1/queue");
        Assert.NotNull(top10);
        Assert.Equal(12, top10.Total);
        Assert.Equal(10, top10.Items.Count);

        // 2. Repeated patients appear as separate rows with distinct EventId + NoteVersion
        Assert.True(top10.Items.Count(i => i.PatientReference == "P930001") >= 3, "Repeated UHID submissions must not collapse to 1 per patient");
        Assert.Equal(10, top10.Items.Select(i => i.EventId).Distinct().Count());

        // 3. Strictly descending by ReceivedAt
        for (int idx = 1; idx < top10.Items.Count; idx++)
        {
            Assert.True(top10.Items[idx - 1].ReceivedAt >= top10.Items[idx].ReceivedAt, "Queue must be ordered newest-first");
        }

        // 4. Failed submission #9 is visible in Top 10 with truthful Failed state and error code
        var failedRow = top10.Items.Single(i => i.NoteId == failedNoteId && i.NoteVersion == failedVersion);
        Assert.Equal("Failed", failedRow.QueueState);
        Assert.Equal("AZURE_INVALID_EVIDENCE", failedRow.Error);
        Assert.Null(failedRow.ExtractionId);

        // 5. Opening failed submission returns exact source text and Failed state (no fabricated extraction)
        var failedDetail = await client.GetFromJsonAsync<QueueEntryDetailDto>($"/api/v1/queue/{failedNoteId}?version={failedVersion}&patient={failedRow.PatientReference}");
        Assert.NotNull(failedDetail);
        Assert.Equal("Failed", failedDetail.QueueState);
        Assert.Equal("AZURE_INVALID_EVIDENCE", failedDetail.Error);
        Assert.Null(failedDetail.Extraction);
        Assert.Contains("#09", failedDetail.SourceText);

        // 6. Pagination offset=10 returns the remaining 2 oldest submissions
        var page2 = await client.GetFromJsonAsync<QueueListResponse>("/api/v1/queue?limit=10&offset=10");
        Assert.NotNull(page2);
        Assert.Equal(12, page2.Total);
        Assert.Equal(2, page2.Items.Count);
        Assert.Empty(top10.Items.Select(i => i.EventId).Intersect(page2.Items.Select(i => i.EventId)));
    }

    [Fact]
    public void Case4_OfflineForensicRegression_ValidCandidatePasses_WhileUngroundedOrMisclassifiedEvidenceIsStrictlyRejected()
    {
        const string case4Source = """
            Chief Complaint:
            Easy bruising and elevated home INR reading.

            Medications:
            Discontinue Warfarin 5 mg immediately due to prolonged prothrombin time.
            Continue Bisoprolol 2.5 mg 1 tablet orally once daily.

            Investigations:
            Previous INR was 4.8 yesterday.
            Order repeat Prothrombin Time and INR today.
            Order Complete Blood Count automated today.

            [Synthetic Case: FOUNDRY-LIVE-04 — Anticoagulation Safety & Discontinuation]
            """;

        var validator = new ClinicalResultValidator();

        // 1. Properly grounded Case 4 candidate passes strict evidence validation cleanly
        var validCandidate = new ClinicalCandidate(
            [
                new("Warfarin", "5 mg", null, null, null, null, null, null, null, "stop", "discontinued",
                    new("Discontinue Warfarin 5 mg immediately due to prolonged prothrombin time.", 1)),
                new("Bisoprolol", "2.5 mg", "1", "tablet", "orally", "once daily", null, "tablet", null, "continue", "current_medication",
                    new("Continue Bisoprolol 2.5 mg 1 tablet orally once daily.", 1)),
            ],
            [
                new("INR", "yesterday", null, "historical", "final",
                    new("Previous INR was 4.8 yesterday.", 1)),
                new("Prothrombin Time", "today", null, "new_order", "preliminary",
                    new("Order repeat Prothrombin Time and INR today.", 1)),
                new("INR", "today", null, "new_order", "preliminary",
                    new("Order repeat Prothrombin Time and INR today.", 1)),
                new("Complete Blood Count automated", "today", null, "new_order", "preliminary",
                    new("Order Complete Blood Count automated today.", 1)),
            ]);

        var mapped = validator.Map(validCandidate, case4Source, "case4-offline-valid");
        Assert.Equal(2, mapped.Medications.Count);
        Assert.Contains(mapped.Medications, m => m.Name == "Warfarin" && m.AssertionStatus == "discontinued");
        Assert.Contains(mapped.Medications, m => m.Name == "Bisoprolol" && m.AssertionStatus == "current_medication");
        Assert.Equal(4, mapped.Investigations.Count);

        // 2. Non-verbatim / paraphrased evidence quote triggers strict InvalidClinicalEvidenceException (AZURE_INVALID_EVIDENCE)
        var nonVerbatimQuoteCandidate = new ClinicalCandidate(
            [
                new("Warfarin", "5 mg", null, null, null, null, null, null, null, "stop", "discontinued",
                    new("Stop Warfarin 5 mg immediately due to prolonged PT.", 1)),
            ],
            []);
        Assert.Throws<InvalidClinicalEvidenceException>(() =>
            validator.Map(nonVerbatimQuoteCandidate, case4Source, "case4-offline-nonverbatim"));

        // 3. Ungrounded historical/final classification on "elevated home INR reading" (lacking historical trigger keyword) triggers strict rejection
        var ungroundedHistoricalCandidate = new ClinicalCandidate(
            [],
            [
                new("INR", null, null, "historical", "final",
                    new("Easy bruising and elevated home INR reading.", 1)),
            ]);
        Assert.Throws<InvalidClinicalEvidenceException>(() =>
            validator.Map(ungroundedHistoricalCandidate, case4Source, "case4-offline-ungrounded-historical"));
    }
}
