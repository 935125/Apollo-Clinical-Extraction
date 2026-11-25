using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Apollo.Backend.Clinical;
using Apollo.Backend.Emr;
using Apollo.Backend.Models;
using Apollo.Backend.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Apollo.Backend.Tests;

public class SyntheticDataPrivacyGateTests
{
    private const string SyntheticAmlNoteWithFictionalIdentifiers = """
        HOSPITAL: Apollo Cancer Centre, Teynampet, Chennai
        PATIENT NAME: Rajeshwari Sundaram | UHID: APL-CHN-8849201 | AGE/SEX: 48 / Female
        ADMISSION DATE: 02/11/2026 | DISCHARGE DATE: 24/11/2026
        ATTENDING ONCOLOGIST: Dr. Karthik Subramanian (Reg. No: TNMC-74912)
        EMERGENCY CONTACT: Venkatraman Sundaram (Spouse) - +91-98401-55219

        PRIMARY DIAGNOSIS:
        Acute Myeloid Leukemia (AML) with NPM1 mutation and FLT3-ITD (low allelic ratio 0.22), ELN 2022 Favorable-to-Intermediate Risk.

        HOSPITAL COURSE:
        Mrs. Rajeshwari Sundaram was admitted for standard "7+3" induction chemotherapy plus FLT3 inhibition.

        DISCHARGE MEDICATIONS:
        1. Posaconazole delayed-release tablets 300 mg PO once daily (antifungal prophylaxis).
        2. Acyclovir 400 mg PO twice daily (antiviral prophylaxis).
        3. Levofloxacin 500 mg PO once daily until ANC > 1,500/uL on two consecutive checks.
        4. Ondansetron 8 mg PO SOS for nausea.

        FOLLOW-UP INSTRUCTIONS:
        Report to Day Care Oncology Wing on 01/12/2026 at 09:00 AM for CBC and renal/liver function panel.
        """;

    private sealed class FakeHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Apollo.Backend.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class StubFoundryExtractor(Func<string, FoundryExtraction> handler) : IFoundryClinicalExtractor
    {
        public int CallCount { get; private set; }
        public string? LastReceivedSource { get; private set; }

        public Task<FoundryExtraction> ExtractAsync(string source, CancellationToken ct)
        {
            CallCount++;
            LastReceivedSource = source;
            return Task.FromResult(handler(source));
        }
    }

    private static SyntheticDataPrivacyPolicy BuildPolicy(
        string envName,
        bool localDemoMode,
        bool allowSynthetic,
        bool allowExternalFoundry,
        string[]? allowlist = null)
    {
        var env = new FakeHostEnvironment(envName);
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:LocalDemoMode"] = localDemoMode ? "true" : "false"
            })
            .Build();
        var opts = Options.Create(new SyntheticDataPrivacyOptions
        {
            AllowAuthorizedSyntheticIdentifiers = allowSynthetic,
            AllowExternalFoundryForVerifiedSynthetic = allowExternalFoundry,
            AllowedPatientReferences = allowlist ?? ["P100234", "P100235", "P100236", "P100237"]
        });
        return new SyntheticDataPrivacyPolicy(env, cfg, opts);
    }

    private static ExtractionPrivacyContext VerifiedContext(string patientRef = "P100234", bool dbSyntheticFlag = true) =>
        new(
            PrivacyProvenanceKind.BackendVerifiedSyntheticEmrRecord,
            PatientId: Guid.NewGuid(),
            PatientReference: patientRef,
            EncounterId: Guid.NewGuid(),
            NoteId: Guid.NewGuid(),
            NoteVersion: 2,
            EventId: Guid.NewGuid(),
            VerifiedSourceHash: EmrHash.Text(SyntheticAmlNoteWithFictionalIdentifiers),
            DatabaseSyntheticFlag: dbSyntheticFlag);

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

    private static async Task<(NoteDto Signed, ClinicalNoteSigned Event)> CreateSignAndPublishAsync(
        HttpClient client,
        IServiceProvider services,
        SimulatedNoteBroker broker,
        Guid encounterId,
        string text)
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
        var signed = (await (await client.SendAsync(sign)).Content.ReadFromJsonAsync<NoteDto>())!;

        var store = services.GetRequiredService<EmrStore>();
        var messaging = services.GetServices<IHostedService>().OfType<EmrMessagingService>().Single();
        await messaging.PublishOnce(store, broker, CancellationToken.None);
        Assert.True(broker.Messages.Reader.TryRead(out var evt));
        return (signed, evt);
    }

    [Fact]
    public void Policy_PermitsVerifiedSyntheticInDevelopment_AndFailsClosedForProductionRealOrUnverified()
    {
        var devPolicy = BuildPolicy("Development", localDemoMode: true, allowSynthetic: true, allowExternalFoundry: false);
        Assert.True(devPolicy.Evaluate(VerifiedContext("P100234"), "LocalOnnx").AllowSyntheticIdentifiers);

        // External Foundry blocked when AllowExternalFoundryForVerifiedSynthetic = false
        var foundryBlocked = devPolicy.Evaluate(VerifiedContext("P100234"), "MicrosoftFoundry");
        Assert.False(foundryBlocked.AllowSyntheticIdentifiers);
        Assert.Equal("STRICT_EXTERNAL_FOUNDRY_SYNTHETIC_DISABLED", foundryBlocked.ReasonCode);

        // Production / Staging ALWAYS fail closed regardless of config or DB flag
        var prodPolicy = BuildPolicy("Production", localDemoMode: true, allowSynthetic: true, allowExternalFoundry: true);
        var prodDecision = prodPolicy.Evaluate(VerifiedContext("P100234"), "LocalOnnx");
        Assert.False(prodDecision.AllowSyntheticIdentifiers);
        Assert.Equal("STRICT_ENVIRONMENT_NOT_DEVELOPMENT", prodDecision.ReasonCode);

        var stagingPolicy = BuildPolicy("Staging", localDemoMode: true, allowSynthetic: true, allowExternalFoundry: true);
        Assert.False(stagingPolicy.Evaluate(VerifiedContext("P100234"), "LocalOnnx").AllowSyntheticIdentifiers);

        // LocalDemoMode=false fails closed
        var nonDemoPolicy = BuildPolicy("Development", localDemoMode: false, allowSynthetic: true, allowExternalFoundry: true);
        Assert.Equal("STRICT_LOCAL_DEMO_MODE_DISABLED", nonDemoPolicy.Evaluate(VerifiedContext("P100234"), "LocalOnnx").ReasonCode);

        // Disabled by default policy fails closed
        var disabledPolicy = BuildPolicy("Development", localDemoMode: true, allowSynthetic: false, allowExternalFoundry: true);
        Assert.Equal("STRICT_SYNTHETIC_POLICY_DISABLED", disabledPolicy.Evaluate(VerifiedContext("P100234"), "LocalOnnx").ReasonCode);

        // Unverified / real records ALWAYS fail closed
        Assert.Equal("STRICT_UNVERIFIED_OR_REAL_RECORD", devPolicy.Evaluate(ExtractionPrivacyContext.Unverified, "LocalOnnx").ReasonCode);
        Assert.Equal("STRICT_UNVERIFIED_OR_REAL_RECORD", devPolicy.Evaluate(VerifiedContext("P100234", dbSyntheticFlag: false), "LocalOnnx").ReasonCode);

        // Unallowlisted patient reference fails closed
        Assert.Equal("STRICT_PATIENT_REFERENCE_NOT_IN_ALLOWLIST", devPolicy.Evaluate(VerifiedContext("P999999"), "LocalOnnx").ReasonCode);
    }

    [PostgresFact]
    public async Task LocalOnnx_PermitsAuthorizedSyntheticNoteWithFictionalHeaders_AndPreservesExactSourceText()
    {
        await using var fixture = new Stage9EmrFixture();
        await fixture.InitializeAsync();
        var (docId, pass) = await fixture.SeedDoctorAsync("syn.ok@apollo.local", "Dr. Synthetic", "Doctor", "SyntheticPass12345!");
        var encId = await fixture.SeedEncounterAsync(docId, "P100234", "ENC-100567");

        var broker = new SimulatedNoteBroker();
        using var factory = new Stage9EmrFactory
        {
            DatabaseConnection = fixture.ConnectionString,
            CustomBroker = broker,
            PrimaryProvider = "LocalOnnx"
        };
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        await LoginAsync(client, "syn.ok@apollo.local", pass);

        var (signed, evt) = await CreateSignAndPublishAsync(client, factory.Services, broker, encId, SyntheticAmlNoteWithFictionalIdentifiers);

        var consumer = factory.Services.GetRequiredService<EmrConsumer>();
        var outcome = await consumer.Process(evt, CancellationToken.None);
        Assert.Equal(ConsumerOutcome.Completed, outcome);

        // Exact original synthetic note (including fictional headers) is preserved in queue detail & extraction
        var detail = await client.GetFromJsonAsync<QueueEntryDetailDto>($"/api/v1/queue/{signed.Id}?version={signed.Version}&patient=P100234");
        Assert.NotNull(detail);
        Assert.Equal("Ready", detail.QueueState);
        Assert.Null(detail.Error);
        Assert.Equal(SyntheticAmlNoteWithFictionalIdentifiers, detail.SourceText);
        Assert.NotNull(detail.Extraction);
        Assert.Equal(SyntheticAmlNoteWithFictionalIdentifiers, detail.Extraction.NoteText);
        Assert.Contains(detail.Extraction.Medications, m => m.Name.Equals("Posaconazole", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(detail.Extraction.Medications, m => m.Name.Equals("Ondansetron", StringComparison.OrdinalIgnoreCase));
    }

    [PostgresFact]
    public async Task LocalOnnx_RejectsUnallowlistedPatientWithIdentifiableText_AsSourceRequiresPrivacyReview()
    {
        await using var fixture = new Stage9EmrFixture();
        await fixture.InitializeAsync();
        var (docId, pass) = await fixture.SeedDoctorAsync("syn.block@apollo.local", "Dr. Block", "Doctor", "BlockPass12345678!");
        // P999888 is NOT in appsettings.Development.json AllowedPatientReferences (P100234..P100237)
        var encId = await fixture.SeedEncounterAsync(docId, "P999888", "ENC-999888");

        var broker = new SimulatedNoteBroker();
        using var factory = new Stage9EmrFactory
        {
            DatabaseConnection = fixture.ConnectionString,
            CustomBroker = broker,
            PrimaryProvider = "LocalOnnx"
        };
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        await LoginAsync(client, "syn.block@apollo.local", pass);

        var (signed, evt) = await CreateSignAndPublishAsync(client, factory.Services, broker, encId, SyntheticAmlNoteWithFictionalIdentifiers);

        var consumer = factory.Services.GetRequiredService<EmrConsumer>();
        var outcome = await consumer.Process(evt, CancellationToken.None);
        Assert.Equal(ConsumerOutcome.DeadLetter, outcome);

        var detail = await client.GetFromJsonAsync<QueueEntryDetailDto>($"/api/v1/queue/{signed.Id}?version={signed.Version}&patient=P999888");
        Assert.NotNull(detail);
        Assert.Equal("Failed", detail.QueueState);
        Assert.Equal("SOURCE_REQUIRES_PRIVACY_REVIEW", detail.Error);
        Assert.Null(detail.Extraction);
    }

    [PostgresFact]
    public async Task DirectApi_NeverTrustsClientSuppliedIsSyntheticFlag_AndRejectsWith422PiiLeakage()
    {
        await using var fixture = new Stage9EmrFixture();
        await fixture.InitializeAsync();

        var broker = new SimulatedNoteBroker();
        using var factory = new Stage9EmrFactory
        {
            DatabaseConnection = fixture.ConnectionString,
            CustomBroker = broker,
            PrimaryProvider = "LocalOnnx"
        };
        using var client = factory.CreateClient();

        // Client attempts to spoof synthetic provenance via JSON field and custom HTTP header
        string spoofedJson = JsonSerializer.Serialize(new
        {
            noteText = SyntheticAmlNoteWithFictionalIdentifiers,
            isSynthetic = true,
            patientReference = "P100234"
        });
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/extractions")
        {
            Content = new StringContent(spoofedJson, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("X-Synthetic-Dataset", "true");

        using var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("PII_LEAKAGE_DETECTED", body);
        Assert.DoesNotContain("Rajeshwari Sundaram", body);
        Assert.DoesNotContain("98401", body);
    }

    [PostgresFact]
    public async Task OfflineFoundryContract_PermitsVerifiedSynthetic_PreservesStrictEvidenceValidation_AndRespectsFoundryToggle()
    {
        await using var fixture = new Stage9EmrFixture();
        await fixture.InitializeAsync();
        var (docId, pass) = await fixture.SeedDoctorAsync("syn.foundry@apollo.local", "Dr. Foundry", "Doctor", "FoundryPass123456!");
        var encId = await fixture.SeedEncounterAsync(docId, "P100234", "ENC-100567");

        // 1. Offline Foundry stub returning valid grounded evidence -> succeeds under authorized Development policy
        var validStub = new StubFoundryExtractor(_ => new FoundryExtraction(
            new ClinicalCandidate(
                [
                    new("Posaconazole", "300 mg", null, null, "PO", "once daily", null, "delayed-release tablets", null, "continue", "current_medication",
                        new("Posaconazole delayed-release tablets 300 mg PO once daily (antifungal prophylaxis).", 1)),
                    new("Ondansetron", "8 mg", null, null, "PO", "SOS", null, null, null, "continue", "current_medication",
                        new("Ondansetron 8 mg PO SOS for nausea.", 1))
                ],
                [
                    new("CBC", "on 01/12/2026 at 09:00 AM", null, "new_order", "preliminary",
                        new("Report to Day Care Oncology Wing on 01/12/2026 at 09:00 AM for CBC and renal/liver function panel.", 1))
                ]),
            new ProviderAttemptDto("foundry", "gpt-6.1-sol-offline-stub", "gpt-6.1-sol", "accepted", null, 12.5, true, PromptTokens: 100, CompletionTokens: 80, TotalTokens: 180)));

        var broker1 = new SimulatedNoteBroker();
        using (var baseFactory = new Stage9EmrFactory
        {
            DatabaseConnection = fixture.ConnectionString,
            CustomBroker = broker1,
            PrimaryProvider = "MicrosoftFoundry"
        })
        using (var factory = baseFactory.WithWebHostBuilder(wb =>
        {
            wb.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Foundry:Endpoint"] = "https://offline-stub.services.ai.azure.com/openai/v1/",
                ["Foundry:Deployment"] = "gpt-6.1-sol",
                ["Foundry:Authentication"] = "ApiKey",
                ["Foundry:ApiKey"] = "offline-stub-key",
                ["Security:SyntheticData:AllowAuthorizedSyntheticIdentifiers"] = "true",
                ["Security:SyntheticData:AllowExternalFoundryForVerifiedSynthetic"] = "true"
            }));
            wb.ConfigureServices(s =>
            {
                s.RemoveAll<IFoundryClinicalExtractor>();
                s.AddSingleton<IFoundryClinicalExtractor>(validStub);
            });
        }))
        {
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
            await LoginAsync(client, "syn.foundry@apollo.local", pass);
            var (signed, evt) = await CreateSignAndPublishAsync(client, factory.Services, broker1, encId, SyntheticAmlNoteWithFictionalIdentifiers);

            var outcome = await factory.Services.GetRequiredService<EmrConsumer>().Process(evt, CancellationToken.None);
            Assert.Equal(ConsumerOutcome.Completed, outcome);
            Assert.Equal(1, validStub.CallCount);
            Assert.Equal(SyntheticAmlNoteWithFictionalIdentifiers, validStub.LastReceivedSource);

            var detail = await client.GetFromJsonAsync<QueueEntryDetailDto>($"/api/v1/queue/{signed.Id}?version={signed.Version}&patient=P100234");
            Assert.NotNull(detail?.Extraction);
            Assert.Equal("foundry", detail.Extraction.ModelMetadata.Provider);
            Assert.Equal(SyntheticAmlNoteWithFictionalIdentifiers, detail.SourceText);
            Assert.Contains(detail.Extraction.Medications, m => m.Name == "Ondansetron" && m.Normalization.MatchedCode == "26225");
        }

        // 2. Offline Foundry stub returning ungrounded quote -> STILL strictly rejected with AZURE_INVALID_EVIDENCE
        var invalidEvidenceStub = new StubFoundryExtractor(_ => new FoundryExtraction(
            new ClinicalCandidate(
                [
                    new("Posaconazole", "300 mg", null, null, "PO", "once daily", null, null, null, "continue", "current_medication",
                        new("Paraphrased non-verbatim quote not in note", 1))
                ],
                []),
            new ProviderAttemptDto("foundry", "gpt-6.1-sol-offline-stub", "gpt-6.1-sol", "accepted", null, 10.0, true)));

        var broker2 = new SimulatedNoteBroker();
        using (var baseFactory = new Stage9EmrFactory
        {
            DatabaseConnection = fixture.ConnectionString,
            CustomBroker = broker2,
            PrimaryProvider = "MicrosoftFoundry"
        })
        using (var factory = baseFactory.WithWebHostBuilder(wb =>
        {
            wb.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Foundry:Endpoint"] = "https://offline-stub.services.ai.azure.com/openai/v1/",
                ["Foundry:Deployment"] = "gpt-6.1-sol",
                ["Foundry:Authentication"] = "ApiKey",
                ["Foundry:ApiKey"] = "offline-stub-key",
                ["Security:SyntheticData:AllowAuthorizedSyntheticIdentifiers"] = "true",
                ["Security:SyntheticData:AllowExternalFoundryForVerifiedSynthetic"] = "true"
            }));
            wb.ConfigureServices(s =>
            {
                s.RemoveAll<IFoundryClinicalExtractor>();
                s.AddSingleton<IFoundryClinicalExtractor>(invalidEvidenceStub);
            });
        }))
        {
            var encId2 = await fixture.SeedEncounterAsync(docId, "P100235", "ENC-100568");
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
            await LoginAsync(client, "syn.foundry@apollo.local", pass);
            var (signed, evt) = await CreateSignAndPublishAsync(client, factory.Services, broker2, encId2, SyntheticAmlNoteWithFictionalIdentifiers);

            var outcome = await factory.Services.GetRequiredService<EmrConsumer>().Process(evt, CancellationToken.None);
            Assert.Equal(ConsumerOutcome.DeadLetter, outcome);

            var detail = await client.GetFromJsonAsync<QueueEntryDetailDto>($"/api/v1/queue/{signed.Id}?version={signed.Version}&patient=P100235");
            Assert.NotNull(detail);
            Assert.Equal("Failed", detail.QueueState);
            Assert.Equal("AZURE_INVALID_EVIDENCE", detail.Error);
        }

        // 3. External Foundry toggle disabled -> blocked BEFORE stub Foundry is ever invoked (0 calls!)
        var neverCalledStub = new StubFoundryExtractor(_ => throw new InvalidOperationException("Foundry must not be invoked when disabled"));
        var broker3 = new SimulatedNoteBroker();
        using (var baseFactory = new Stage9EmrFactory
        {
            DatabaseConnection = fixture.ConnectionString,
            CustomBroker = broker3,
            PrimaryProvider = "MicrosoftFoundry"
        })
        using (var factory = baseFactory.WithWebHostBuilder(wb =>
        {
            wb.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Foundry:Endpoint"] = "https://offline-stub.services.ai.azure.com/openai/v1/",
                ["Foundry:Deployment"] = "gpt-6.1-sol",
                ["Foundry:Authentication"] = "ApiKey",
                ["Foundry:ApiKey"] = "offline-stub-key",
                ["Security:SyntheticData:AllowAuthorizedSyntheticIdentifiers"] = "true",
                ["Security:SyntheticData:AllowExternalFoundryForVerifiedSynthetic"] = "false"
            }));
            wb.ConfigureServices(s =>
            {
                s.RemoveAll<IFoundryClinicalExtractor>();
                s.AddSingleton<IFoundryClinicalExtractor>(neverCalledStub);
            });
        }))
        {
            var encId3 = await fixture.SeedEncounterAsync(docId, "P100236", "ENC-100569");
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
            await LoginAsync(client, "syn.foundry@apollo.local", pass);
            var (signed, evt) = await CreateSignAndPublishAsync(client, factory.Services, broker3, encId3, SyntheticAmlNoteWithFictionalIdentifiers);

            var outcome = await factory.Services.GetRequiredService<EmrConsumer>().Process(evt, CancellationToken.None);
            Assert.Equal(ConsumerOutcome.DeadLetter, outcome);
            Assert.Equal(0, neverCalledStub.CallCount);

            var detail = await client.GetFromJsonAsync<QueueEntryDetailDto>($"/api/v1/queue/{signed.Id}?version={signed.Version}&patient=P100236");
            Assert.NotNull(detail);
            Assert.Equal("Failed", detail.QueueState);
            Assert.Equal("SOURCE_REQUIRES_PRIVACY_REVIEW", detail.Error);
        }
    }
}
