using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Apollo.Backend.Emr;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Apollo.Backend.Tests;

public class Stage12_1UhidIsolationTests
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

    private static async Task<NoteDto> CreateAndSignAsync(HttpClient client, Guid encounterId, string text)
    {
        string csrf = await FetchCsrfAsync(client);
        var create = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/emr/encounters/{encounterId}/notes")
        {
            Content = JsonContent.Create(new DraftRequest(text))
        };
        create.Headers.Add("X-XSRF-TOKEN", csrf);
        var draft = (await (await client.SendAsync(create)).Content.ReadFromJsonAsync<NoteDto>())!;

        csrf = await FetchCsrfAsync(client);
        var sign = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/emr/notes/{draft.Id}/sign")
        {
            Content = JsonContent.Create(new SignRequest(draft.Version))
        };
        sign.Headers.Add("X-XSRF-TOKEN", csrf);
        return (await (await client.SendAsync(sign)).Content.ReadFromJsonAsync<NoteDto>())!;
    }

    private static async Task<NoteDto> CorrectAndSignAsync(HttpClient client, Guid noteId, int expectedVersion, string text)
    {
        string csrf = await FetchCsrfAsync(client);
        var corr = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/emr/notes/{noteId}/corrections")
        {
            Content = JsonContent.Create(new DraftRequest(text, expectedVersion))
        };
        corr.Headers.Add("X-XSRF-TOKEN", csrf);
        var draft = (await (await client.SendAsync(corr)).Content.ReadFromJsonAsync<NoteDto>())!;

        csrf = await FetchCsrfAsync(client);
        var sign = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/emr/notes/{draft.Id}/sign")
        {
            Content = JsonContent.Create(new SignRequest(draft.Version))
        };
        sign.Headers.Add("X-XSRF-TOKEN", csrf);
        return (await (await client.SendAsync(sign)).Content.ReadFromJsonAsync<NoteDto>())!;
    }

    private static async Task DrainQueueAsync(Stage9EmrFactory factory, SimulatedNoteBroker broker)
    {
        var store = factory.Services.GetRequiredService<EmrStore>();
        var messaging = factory.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
            .OfType<EmrMessagingService>().Single();
        var consumer = factory.Services.GetRequiredService<EmrConsumer>();

        await messaging.PublishOnce(store, broker, CancellationToken.None);
        while (broker.Messages.Reader.TryRead(out var evt))
        {
            Assert.Equal(ConsumerOutcome.Completed, await consumer.Process(evt, CancellationToken.None));
        }
    }

    [PostgresFact]
    public async Task SinglePatient_MultipleEncountersAndVersions_NeverMixesExtractions_AndPreservesBackendValidation()
    {
        await using var fixture = new Stage9EmrFixture();
        await fixture.InitializeAsync();
        var (docA, passA) = await fixture.SeedDoctorAsync("iso.a@apollo.local", "Dr. Iso A", "Doctor", "IsoAPassword123!");
        var (docB, passB) = await fixture.SeedDoctorAsync("iso.b@apollo.local", "Dr. Iso B", "Doctor", "IsoBPassword123!");

        // One patient (P910001) with TWO encounters assigned to Doctor A
        var enc1 = await fixture.SeedEncounterAsync(docA, "P910001", "ENC-910001");
        var enc2 = await fixture.SeedEncounterAsync(docA, "P910001", "ENC-910002");
        // Separate patient (P910002) assigned to Doctor B
        var encB = await fixture.SeedEncounterAsync(docB, "P910002", "ENC-910003");

        var broker = new SimulatedNoteBroker();
        using var factory = new Stage9EmrFactory { DatabaseConnection = fixture.ConnectionString, CustomBroker = broker, PrimaryProvider = "LocalOnnx" };

        using var clientA = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        await LoginAsync(clientA, "iso.a@apollo.local", passA);

        using var clientB = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        await LoginAsync(clientB, "iso.b@apollo.local", passB);

        // Encounter 1 Version 2: Amlodipine ONLY
        var note1v2 = await CreateAndSignAsync(clientA, enc1, "Chief Complaint: BP review.\nPlan: Continue Amlodipine 5 mg 1 tablet orally once daily for 30 days.");
        Assert.Equal(2, note1v2.Version);
        await DrainQueueAsync(factory, broker);

        // Encounter 1 Version 4: Metformin ONLY (immutable correction)
        var note1v4 = await CorrectAndSignAsync(clientA, note1v2.Id, note1v2.Version, "Chief Complaint: Glycemic review.\nPlan: Start Metformin 500 mg 1 tablet orally twice daily for 14 days.");
        Assert.Equal(4, note1v4.Version);
        await DrainQueueAsync(factory, broker);

        // Encounter 2 Version 2: Atorvastatin ONLY
        var note2v2 = await CreateAndSignAsync(clientA, enc2, "Chief Complaint: Lipid check.\nPlan: Start Atorvastatin 20 mg 1 tablet orally at bedtime for 30 days.");
        await DrainQueueAsync(factory, broker);

        // Patient B (Doctor B): Warfarin ONLY
        var noteB = await CreateAndSignAsync(clientB, encB, "Chief Complaint: INR follow-up.\nPlan: Continue Warfarin 5 mg 1 tablet orally daily.");
        await DrainQueueAsync(factory, broker);

        // 1. Fetch P910001 Note 1 Version 2 -> MUST have Amlodipine ONLY (no Metformin, no Atorvastatin, no Warfarin)
        var detailV2 = await clientA.GetFromJsonAsync<QueueEntryDetailDto>($"/api/v1/queue/{note1v2.Id}?version=2&patient=P910001");
        Assert.NotNull(detailV2);
        Assert.Equal("P910001", detailV2.PatientReference);
        Assert.Equal(2, detailV2.NoteVersion);
        Assert.NotNull(detailV2.Extraction);
        Assert.NotNull(detailV2.ExtractionId);
        Assert.Contains(detailV2.Extraction.Medications, m => m.Name.Contains("Amlodipine", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(detailV2.Extraction.Medications, m => m.Name.Contains("Metformin", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(detailV2.Extraction.Medications, m => m.Name.Contains("Atorvastatin", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(detailV2.Extraction.Medications, m => m.Name.Contains("Warfarin", StringComparison.OrdinalIgnoreCase));

        // 2. Fetch P910001 Note 1 Version 4 -> MUST have Metformin ONLY and DIFFERENT ExtractionId from Version 2
        var detailV4 = await clientA.GetFromJsonAsync<QueueEntryDetailDto>($"/api/v1/queue/{note1v2.Id}?version=4&patient=P910001");
        Assert.NotNull(detailV4);
        Assert.Equal(4, detailV4.NoteVersion);
        Assert.NotNull(detailV4.Extraction);
        Assert.NotEqual(detailV2.ExtractionId, detailV4.ExtractionId);
        Assert.Contains(detailV4.Extraction.Medications, m => m.Name.Contains("Metformin", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(detailV4.Extraction.Medications, m => m.Name.Contains("Amlodipine", StringComparison.OrdinalIgnoreCase));

        // 3. Within-patient note selector contains ONLY P910001's 3 signed versions across ENC-910001 & ENC-910002 (never ENC-910003 / P910002)
        Assert.NotNull(detailV4.PatientNotes);
        Assert.Equal(3, detailV4.PatientNotes.Count);
        Assert.All(detailV4.PatientNotes, pn => Assert.NotEqual("ENC-910003", pn.EncounterReference));
        Assert.Contains(detailV4.PatientNotes, pn => pn.EncounterReference == "ENC-910001" && pn.NoteVersion == 2);
        Assert.Contains(detailV4.PatientNotes, pn => pn.EncounterReference == "ENC-910001" && pn.NoteVersion == 4);
        Assert.Contains(detailV4.PatientNotes, pn => pn.EncounterReference == "ENC-910002" && pn.NoteVersion == 2);

        // 4. Backend validation warnings & provider provenance remain active and persisted in API response
        Assert.Equal("onnx", detailV4.Extraction.ModelMetadata.Provider);
        Assert.True(detailV4.Extraction.ValidationSummary.WarningCount > 0, "Backend validation must remain active even when frontend hides warning panels");

        // 5. Cross-patient & tampered parameter enforcement on server
        Assert.Equal(HttpStatusCode.NotFound, (await clientA.GetAsync($"/api/v1/queue/{note1v2.Id}?patient=P910002")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clientA.GetAsync($"/api/v1/queue/{noteB.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clientA.GetAsync("/api/v1/queue/patients/P910002")).StatusCode);

        var detailB = await clientB.GetFromJsonAsync<QueueEntryDetailDto>($"/api/v1/queue/{noteB.Id}");
        Assert.NotNull(detailB?.ExtractionId);
        var crossExtResp = await clientA.GetAsync($"/api/v1/extractions/{detailB.ExtractionId}");
        Assert.True(
            crossExtResp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden,
            $"Cross-patient extraction access must be denied; got {crossExtResp.StatusCode}");

        // 6. Authorized patient scope endpoint returns ONLY P910001
        var scopeResp = await clientA.GetAsync("/api/v1/queue/patients/P910001");
        Assert.Equal(HttpStatusCode.OK, scopeResp.StatusCode);
        var scopeJson = await scopeResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("P910001", scopeJson.GetProperty("patientReference").GetString());
        Assert.Equal(3, scopeJson.GetProperty("notes").GetArrayLength());
    }
}
