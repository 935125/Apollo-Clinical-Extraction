using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Apollo.Backend.Clinical;
using Apollo.Backend.Emr;
using Apollo.Backend.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Apollo.Backend.Tests;

public class Stage12QueueTests
{
    private static async Task<string> FetchCsrfTokenAsync(HttpClient client)
    {
        var resp = await client.GetAsync("/api/v1/emr/auth/csrf");
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("token").GetString()!;
    }

    [PostgresFact]
    public async Task QueueApi_RequiresAuthentication_AndEnforcesStrictEncounterIsolation()
    {
        await using var fixture = new Stage9EmrFixture();
        await fixture.InitializeAsync();
        var (docA, passA) = await fixture.SeedDoctorAsync("queue.a@apollo.local", "Dr. Queue A", "Doctor", "QueueAPassword123!");
        var (docB, passB) = await fixture.SeedDoctorAsync("queue.b@apollo.local", "Dr. Queue B", "Doctor", "QueueBPassword123!");
        var encA = await fixture.SeedEncounterAsync(docA, "P900101", "ENC-900101");
        var encB = await fixture.SeedEncounterAsync(docB, "P900102", "ENC-900102");

        var simBroker = new SimulatedNoteBroker();
        using var factory = new Stage9EmrFactory { DatabaseConnection = fixture.ConnectionString, CustomBroker = simBroker, PrimaryProvider = "LocalOnnx" };

        // 1. Unauthenticated access to /api/v1/queue MUST return 401
        using var unauth = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var unauthResp = await unauth.GetAsync("/api/v1/queue");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthResp.StatusCode);

        // 2. Doctor A logs in and creates + signs a long clinical note (> 200 chars)
        using var clientA = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        string csrfA = await FetchCsrfTokenAsync(clientA);
        var loginA = new HttpRequestMessage(HttpMethod.Post, "/api/v1/emr/auth/login") { Content = JsonContent.Create(new LoginRequest("queue.a@apollo.local", passA)) };
        loginA.Headers.Add("X-XSRF-TOKEN", csrfA);
        (await clientA.SendAsync(loginA)).EnsureSuccessStatusCode();

        string longNoteA = "Chief Complaint:\nFollow-up for hypertension.\n\nMedications:\nContinue amlodipine 5 mg 1 tablet orally once daily for 30 days. Start metformin 500 mg half tablet orally twice daily for 14 days.\n\nInvestigations:\nOrder complete blood count automated and serum creatinine tomorrow. " + new string('X', 220);
        csrfA = await FetchCsrfTokenAsync(clientA);
        var draftA = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/emr/encounters/{encA}/notes") { Content = JsonContent.Create(new DraftRequest(longNoteA)) };
        draftA.Headers.Add("X-XSRF-TOKEN", csrfA);
        var noteA = (await (await clientA.SendAsync(draftA)).Content.ReadFromJsonAsync<NoteDto>())!;

        csrfA = await FetchCsrfTokenAsync(clientA);
        var signA = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/emr/notes/{noteA.Id}/sign") { Content = JsonContent.Create(new SignRequest(1)) };
        signA.Headers.Add("X-XSRF-TOKEN", csrfA);
        (await clientA.SendAsync(signA)).EnsureSuccessStatusCode();

        // 3. Doctor B logs in and creates a note
        using var clientB = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        string csrfB = await FetchCsrfTokenAsync(clientB);
        var loginB = new HttpRequestMessage(HttpMethod.Post, "/api/v1/emr/auth/login") { Content = JsonContent.Create(new LoginRequest("queue.b@apollo.local", passB)) };
        loginB.Headers.Add("X-XSRF-TOKEN", csrfB);
        (await clientB.SendAsync(loginB)).EnsureSuccessStatusCode();

        csrfB = await FetchCsrfTokenAsync(clientB);
        var draftB = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/emr/encounters/{encB}/notes") { Content = JsonContent.Create(new DraftRequest("Doctor B private note: atorvastatin 20 mg at night.")) };
        draftB.Headers.Add("X-XSRF-TOKEN", csrfB);
        var noteB = (await (await clientB.SendAsync(draftB)).Content.ReadFromJsonAsync<NoteDto>())!;

        // 4. Doctor A queries /api/v1/queue -> sees ONLY noteA, compact preview (<= 180 chars), state = Waiting
        var queueRespA = await clientA.GetFromJsonAsync<QueueListResponse>("/api/v1/queue");
        Assert.NotNull(queueRespA);
        Assert.Equal(1, queueRespA.Total);
        Assert.Single(queueRespA.Items);
        Assert.Equal(noteA.Id, queueRespA.Items[0].NoteId);
        Assert.True(queueRespA.Items[0].SourcePreview.Length <= 180, "Queue list must return compact preview, not full note text");
        Assert.Equal("Waiting", queueRespA.Items[0].QueueState);

        // 5. Doctor A CANNOT open Doctor B's queue entry (404)
        var crossResp = await clientA.GetAsync($"/api/v1/queue/{noteB.Id}");
        Assert.Equal(HttpStatusCode.NotFound, crossResp.StatusCode);

        // 6. Publish & consume Doctor A's note through outbox -> broker -> worker -> LocalOnnx
        var store = factory.Services.GetRequiredService<EmrStore>();
        var messaging = factory.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<EmrMessagingService>().Single();
        await messaging.PublishOnce(store, simBroker, CancellationToken.None);
        Assert.True(simBroker.Messages.Reader.TryRead(out var evt));
        var consumer = factory.Services.GetRequiredService<EmrConsumer>();
        Assert.Equal(ConsumerOutcome.Completed, await consumer.Process(evt, CancellationToken.None));

        // 7. Re-poll /api/v1/queue -> state is now Ready with LocalOnnx provenance
        var afterRespA = await clientA.GetFromJsonAsync<QueueListResponse>("/api/v1/queue?search=P900101");
        Assert.NotNull(afterRespA);
        Assert.Single(afterRespA.Items);
        Assert.Equal("Ready", afterRespA.Items[0].QueueState);
        Assert.Equal("onnx", afterRespA.Items[0].Provider);
        Assert.NotNull(afterRespA.Items[0].ExtractionId);

        // 8. Open detail /api/v1/queue/{noteId} -> full immutable note text, SHA-256 hash, extraction results, zero duplicate finding codes per entity
        var detail = await clientA.GetFromJsonAsync<QueueEntryDetailDto>($"/api/v1/queue/{noteA.Id}");
        Assert.NotNull(detail);
        Assert.Equal(longNoteA, detail.SourceText);
        Assert.Equal(64, detail.SourceHash?.Length);
        Assert.NotNull(detail.Extraction);
        Assert.NotEmpty(detail.Extraction.Medications);
        Assert.All(detail.Extraction.Medications, m =>
            Assert.Equal(m.Findings.Select(f => f.Code).Distinct().Count(), m.Findings.Count));
        Assert.All(detail.Extraction.Investigations, i =>
            Assert.Equal(i.Findings.Select(f => f.Code).Distinct().Count(), i.Findings.Count));
    }

    [PostgresFact]
    public async Task DevSessionEndpoint_CreatesRealServerSideSession_InDevelopmentLoopback()
    {
        await using var fixture = new Stage9EmrFixture();
        await fixture.InitializeAsync();
        await fixture.SeedDoctorAsync("doctor.krishnan@apollo.local", "Dr. K. Krishnan", "Doctor", "DevSeedPass123456!");

        using var factory = new Stage9EmrFactory { DatabaseConnection = fixture.ConnectionString, PrimaryProvider = "LocalOnnx" };
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

        var devResp = await client.PostAsync("/api/v1/emr/auth/dev-session", null);
        Assert.Equal(HttpStatusCode.OK, devResp.StatusCode);
        var profile = await devResp.Content.ReadFromJsonAsync<DoctorProfile>();
        Assert.Equal("Dr. K. Krishnan", profile?.DisplayName);

        // Subsequent protected /api/v1/queue request succeeds using the issued cookie session
        var queueResp = await client.GetAsync("/api/v1/queue");
        Assert.Equal(HttpStatusCode.OK, queueResp.StatusCode);
    }

    [Fact]
    public void ExtractionDefects_DeduplicatedInvestigationFindings_ExpandedDose_AndCbcLoinc()
    {
        // 1. Expanded administered dose binding ("half tablet", "1 tablet")
        var binder = new PosologyBinder();
        const string note = "Start metformin 500 mg half tablet orally twice daily for 14 days.";
        int idx = note.IndexOf("metformin", StringComparison.OrdinalIgnoreCase);
        var bound = binder.Bind(note, idx, idx + "metformin".Length, []);
        Assert.Equal("500 mg", bound.Strength);
        Assert.Equal("half tablet", bound.Dose);
        Assert.Equal("PO", bound.Route);
        Assert.Equal("BID", bound.Frequency);

        // 2. Defensible CBC LOINC mapping: exact when automated count is explicit; narrower with 58410-2 candidate when generic
        Assert.True(TerminologyVerification.InvestigationSupported("complete blood count automated", "58410-2"));
        var (genericSem, genericNote) = TerminologyVerification.ClassifyInvestigationSemantic("Complete Blood Count", "58410-2");
        Assert.Equal("narrower", genericSem);
        Assert.Contains("58410-2", genericNote);

        // 3. ClinicalReview.Apply idempotency: running Apply twice must never duplicate investigation flags/findings
        var sampleInv = new ExtractedInvestigationDto(
            "Complete Blood Count", null, null, "new_order",
            new EvidenceDto("Order complete blood count.", true, 0, 27),
            [], [],
            new NormalizationDto("uncertain", null, "Complete Blood Count", 0.9, "narrower", "58410-2", "LOINC", "2.83", genericNote),
            null, "Complete Blood Count", "preliminary", "laboratory");
        var resp = new ClinicalExtractionResponse(
            "1.0.0", "id-1", "corr-1", DateTimeOffset.UtcNow.ToString("O"), 12.0,
            new ModelMetadataDto("onnx", "bc5cdr_int8", "v1"),
            new PreprocessedDto(27, []),
            [], [sampleInv],
            new ValidationSummaryDto(false, 0, 0), []);
        var once = ClinicalReview.Apply(resp, reducedCoverage: true);
        var twice = ClinicalReview.Apply(once, reducedCoverage: true);
        Assert.Equal(once.Investigations[0].QualityFlags.Count, twice.Investigations[0].QualityFlags.Count);
        Assert.Equal(once.Investigations[0].Findings.Count, twice.Investigations[0].Findings.Count);
    }
}
