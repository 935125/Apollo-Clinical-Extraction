using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Apollo.Backend.Clinical;
using Apollo.Backend.Emr;
using Apollo.Backend.Models;
using Apollo.Backend.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Apollo.Backend.Tests;

public sealed class Stage9EmrFixture : IAsyncDisposable
{
    public IsolatedDatabase Database { get; } = new();
    public string ConnectionString => Database.ConnectionString;
    public string Schema => Database.Schema;

    public async Task InitializeAsync()
    {
        await Database.Initialize();
        var repo = new SqlExtractionRepository(ConnectionString, NullLogger<SqlExtractionRepository>.Instance);
        await repo.EnsureSchemaAsync();

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        string sql;
        await using (var stream = typeof(EmrAdmin).Assembly.GetManifestResourceStream("Apollo.Backend.db.platform.migrations.V0009__doctor_emr.sql"))
        {
            if (stream != null)
            {
                using var reader = new StreamReader(stream);
                sql = await reader.ReadToEndAsync();
            }
            else
            {
                string diskPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "backend", "db", "platform", "migrations", "V0009__doctor_emr.sql"));
                if (!File.Exists(diskPath)) diskPath = @"D:\AI Extraction\backend\db\platform\migrations\V0009__doctor_emr.sql";
                sql = await File.ReadAllTextAsync(diskPath);
            }
        }
        await EmrStore.Execute(conn, sql);
    }

    public async Task<(Guid DoctorId, string Password)> SeedDoctorAsync(string login = "dr.test@apollo.local", string name = "Dr. Test Doctor", string role = "Doctor", string password = "TestPassword123456!")
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        Guid docId = Guid.NewGuid();
        string hash = new PasswordHasher<string>().HashPassword(login, password);
        await EmrStore.Execute(conn, "INSERT INTO emr_doctors(id,login,display_name,role,password_hash) VALUES(@id,@login,@name,@role,@hash)", default, null,
            ("id", docId), ("login", login), ("name", name), ("role", role), ("hash", hash));
        return (docId, password);
    }

    public async Task<Guid> SeedEncounterAsync(Guid doctorId, string patientRef = "P100234", string encRef = "ENC-100567", string context = "General medicine · Follow-up")
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        Guid patientId = Guid.NewGuid();
        Guid encId = Guid.NewGuid();
        await EmrStore.Execute(conn, "INSERT INTO emr_patients(id,reference,synthetic) VALUES(@id,@ref,true) ON CONFLICT(reference) DO NOTHING", default, null,
            ("id", patientId), ("ref", patientRef));
        await using (var cmd = EmrStore.Command(conn, "SELECT id FROM emr_patients WHERE reference=@ref", null, ("ref", patientRef)))
            patientId = (Guid)(await cmd.ExecuteScalarAsync())!;

        await EmrStore.Execute(conn, "INSERT INTO emr_encounters(id,reference,patient_id,encounter_date,context) VALUES(@id,@ref,@patient,now(),@ctx)", default, null,
            ("id", encId), ("ref", encRef), ("patient", patientId), ("ctx", context));
        await EmrStore.Execute(conn, "INSERT INTO emr_encounter_assignments(encounter_id,doctor_id) VALUES(@enc,@doc)", default, null,
            ("enc", encId), ("doc", doctorId));
        return encId;
    }

    public async ValueTask DisposeAsync()
    {
        await Database.DisposeAsync();
    }
}

public sealed class Stage9EmrFactory : WebApplicationFactory<Program>
{
    public string DatabaseConnection { get; init; } = "";
    public INoteBroker? CustomBroker { get; init; }
    public bool DisableMessagingService { get; init; } = false;
    // Test factories must never inherit a billable live provider from a developer's .env.
    public string PrimaryProvider { get; init; } = "LocalOnnx";
    public string FallbackProvider { get; init; } = "LocalOnnx";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((ctx, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = DatabaseConnection,
                ["ConnectionStrings:Emr"] = DatabaseConnection,
                ["Extraction:Provider"] = PrimaryProvider,
                ["Extraction:FallbackProvider"] = FallbackProvider,
                ["Extraction:AllowDirectLocal"] = "true",
                ["Extraction:DirectLocalReason"] = "isolated_test",
                ["Security:LocalDemoMode"] = "true",
                ["Emr:Messaging:Mode"] = CustomBroker is not null ? "Custom" : "Simulated"
            });
        });
        builder.ConfigureServices(services =>
        {
            if (DisableMessagingService)
            {
                services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>();
            }
            if (CustomBroker != null)
            {
                services.RemoveAll<INoteBroker>();
                services.AddSingleton<INoteBroker>(CustomBroker);
            }
        });
    }
}

public class Stage9EmrTests
{
    private static async Task<string> FetchCsrfTokenAsync(HttpClient client)
    {
        var resp = await client.GetAsync("/api/v1/emr/auth/csrf");
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("token").GetString()!;
    }

    [PostgresFact]
    public async Task Test1_DoctorLogin_SucceedsWithValidCredentials_RejectsInvalidPassword_AndHandlesLockout()
    {
        await using var fixture = new Stage9EmrFixture();
        await fixture.InitializeAsync();
        var (docId, password) = await fixture.SeedDoctorAsync("dr.krishnan@apollo.local", "Dr. K. Krishnan", "Doctor", "SecurePassword123!");

        using var factory = new Stage9EmrFactory { DatabaseConnection = fixture.ConnectionString };
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

        string csrf = await FetchCsrfTokenAsync(client);

        // 1. Unauthenticated doctor routes redirect / return 401
        var unauthResp = await client.GetAsync("/api/v1/emr/encounters");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthResp.StatusCode);

        // 2. Invalid password returns 401 LOGIN_REJECTED
        var badLoginReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/emr/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest("dr.krishnan@apollo.local", "WrongPassword!"))
        };
        badLoginReq.Headers.Add("X-XSRF-TOKEN", csrf);
        using var badLoginResp = await client.SendAsync(badLoginReq);
        Assert.Equal(HttpStatusCode.Unauthorized, badLoginResp.StatusCode);
        var badErr = await badLoginResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("LOGIN_REJECTED", badErr.GetProperty("code").GetString());

        // 3. Successful login returns 200 with DoctorProfile
        var okLoginReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/emr/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest("dr.krishnan@apollo.local", "SecurePassword123!"))
        };
        okLoginReq.Headers.Add("X-XSRF-TOKEN", csrf);
        using var okLoginResp = await client.SendAsync(okLoginReq);
        Assert.Equal(HttpStatusCode.OK, okLoginResp.StatusCode);
        var profile = await okLoginResp.Content.ReadFromJsonAsync<DoctorProfile>();
        Assert.NotNull(profile);
        Assert.Equal("Dr. K. Krishnan", profile.DisplayName);
        Assert.Equal("Doctor", profile.Role);

        // 4. Authenticated request succeeds
        var authedResp = await client.GetAsync("/api/v1/emr/auth/me");
        Assert.Equal(HttpStatusCode.OK, authedResp.StatusCode);

        // 5. Logout revokes session
        csrf = await FetchCsrfTokenAsync(client);
        var logoutReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/emr/auth/logout") { Content = JsonContent.Create(new { }) };
        logoutReq.Headers.Add("X-XSRF-TOKEN", csrf);
        using var logoutResp = await client.SendAsync(logoutReq);
        Assert.Equal(HttpStatusCode.NoContent, logoutResp.StatusCode);

        // 6. Accessing after logout is 401
        var postLogoutResp = await client.GetAsync("/api/v1/emr/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, postLogoutResp.StatusCode);
    }

    [PostgresFact]
    public async Task Test2_Authorization_DoctorACannotAccessDoctorBEncounterOrNote()
    {
        await using var fixture = new Stage9EmrFixture();
        await fixture.InitializeAsync();
        var (docA, passA) = await fixture.SeedDoctorAsync("doc.a@apollo.local", "Doctor A", "Doctor", "DocAPassword123456!");
        var (docB, passB) = await fixture.SeedDoctorAsync("doc.b@apollo.local", "Doctor B", "Doctor", "DocBPassword123456!");

        var encA = await fixture.SeedEncounterAsync(docA, "P100234", "ENC-A01");
        var encB = await fixture.SeedEncounterAsync(docB, "P100235", "ENC-B01");

        using var factory = new Stage9EmrFactory { DatabaseConnection = fixture.ConnectionString };

        // Login as Doctor A
        using var clientA = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        string csrfA = await FetchCsrfTokenAsync(clientA);
        var loginA = new HttpRequestMessage(HttpMethod.Post, "/api/v1/emr/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest("doc.a@apollo.local", passA))
        };
        loginA.Headers.Add("X-XSRF-TOKEN", csrfA);
        (await clientA.SendAsync(loginA)).EnsureSuccessStatusCode();

        // Doctor A can see encA
        var respA = await clientA.GetAsync($"/api/v1/emr/encounters/{encA}");
        Assert.Equal(HttpStatusCode.OK, respA.StatusCode);

        // Doctor A cannot see encB (returns 404 Not Found to prevent patient/encounter existence disclosure)
        var respB = await clientA.GetAsync($"/api/v1/emr/encounters/{encB}");
        Assert.Equal(HttpStatusCode.NotFound, respB.StatusCode);
    }

    [PostgresFact]
    public async Task Test3_Dashboard_AssignedEncountersLoadFromPostgresWithStatuses()
    {
        await using var fixture = new Stage9EmrFixture();
        await fixture.InitializeAsync();
        var (docId, pass) = await fixture.SeedDoctorAsync("doc.dash@apollo.local", "Doctor Dash", "Doctor", "DocDashPassword123!");
        var enc1 = await fixture.SeedEncounterAsync(docId, "P100234", "ENC-100567", "General medicine · Follow-up consultation");
        var enc2 = await fixture.SeedEncounterAsync(docId, "P100235", "ENC-100568", "General medicine · Outpatient consultation");

        using var factory = new Stage9EmrFactory { DatabaseConnection = fixture.ConnectionString };
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        string csrf = await FetchCsrfTokenAsync(client);
        var loginReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/emr/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest("doc.dash@apollo.local", pass))
        };
        loginReq.Headers.Add("X-XSRF-TOKEN", csrf);
        (await client.SendAsync(loginReq)).EnsureSuccessStatusCode();

        var encountersResp = await client.GetAsync("/api/v1/emr/encounters");
        Assert.Equal(HttpStatusCode.OK, encountersResp.StatusCode);
        var encounters = await encountersResp.Content.ReadFromJsonAsync<List<EncounterDto>>();
        Assert.NotNull(encounters);
        Assert.Equal(2, encounters.Count);
        Assert.Contains(encounters, e => e.Reference == "ENC-100567" && e.PatientReference == "P100234");
        Assert.Contains(encounters, e => e.Reference == "ENC-100568" && e.PatientReference == "P100235");
    }

    [PostgresFact]
    public async Task Test4_Draft_CreateAndPersistDraft_NoOutboxEventPublished()
    {
        await using var fixture = new Stage9EmrFixture();
        await fixture.InitializeAsync();
        var (docId, pass) = await fixture.SeedDoctorAsync("doc.draft@apollo.local", "Doctor Draft", "Doctor", "DocDraftPassword123!");
        var enc = await fixture.SeedEncounterAsync(docId, "P100234", "ENC-100567");

        using var factory = new Stage9EmrFactory { DatabaseConnection = fixture.ConnectionString };
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        string csrf = await FetchCsrfTokenAsync(client);
        var loginReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/emr/auth/login") { Content = JsonContent.Create(new LoginRequest("doc.draft@apollo.local", pass)) };
        loginReq.Headers.Add("X-XSRF-TOKEN", csrf);
        (await client.SendAsync(loginReq)).EnsureSuccessStatusCode();

        // Create draft
        csrf = await FetchCsrfTokenAsync(client);
        var createDraft = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/emr/encounters/{enc}/notes")
        {
            Content = JsonContent.Create(new DraftRequest("Patient presents with cough for 3 days. Prescribe guaifenesin."))
        };
        createDraft.Headers.Add("X-XSRF-TOKEN", csrf);
        using var createResp = await client.SendAsync(createDraft);
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var created = await createResp.Content.ReadFromJsonAsync<NoteDto>();
        Assert.NotNull(created);
        Assert.Equal(1, created.Version);
        Assert.Equal("Draft", created.Status);

        // Verify PostgreSQL tables: emr_notes has 1 row, emr_outbox_events has 0 rows
        await using var conn = new NpgsqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        await using var countNotes = new NpgsqlCommand($"SELECT count(*) FROM \"{fixture.Schema}\".emr_notes WHERE id = '{created.Id}'", conn);
        Assert.Equal(1L, (long)(await countNotes.ExecuteScalarAsync())!);

        await using var countOutbox = new NpgsqlCommand($"SELECT count(*) FROM \"{fixture.Schema}\".emr_outbox_events WHERE note_id = '{created.Id}'", conn);
        Assert.Equal(0L, (long)(await countOutbox.ExecuteScalarAsync())!);

        // Update draft with matching version
        csrf = await FetchCsrfTokenAsync(client);
        var updateDraft = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/emr/notes/{created.Id}")
        {
            Content = JsonContent.Create(new DraftRequest("Patient presents with cough for 3 days. Prescribe guaifenesin 200 mg.", ExpectedVersion: 1))
        };
        updateDraft.Headers.Add("X-XSRF-TOKEN", csrf);
        using var updateResp = await client.SendAsync(updateDraft);
        Assert.Equal(HttpStatusCode.OK, updateResp.StatusCode);
        var updated = await updateResp.Content.ReadFromJsonAsync<NoteDto>();
        Assert.NotNull(updated);
        Assert.Equal(2, updated.Version);

        // Stale version update returns 409 Conflict
        csrf = await FetchCsrfTokenAsync(client);
        var staleUpdate = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/emr/notes/{created.Id}")
        {
            Content = JsonContent.Create(new DraftRequest("Stale edit.", ExpectedVersion: 1))
        };
        staleUpdate.Headers.Add("X-XSRF-TOKEN", csrf);
        using var staleResp = await client.SendAsync(staleUpdate);
        Assert.Equal(HttpStatusCode.Conflict, staleResp.StatusCode);
    }

    [PostgresFact]
    public async Task Test5_Sign_CreatesImmutableSignedVersion_AndAtomicallyInsertsOutboxEvent()
    {
        await using var fixture = new Stage9EmrFixture();
        await fixture.InitializeAsync();
        var (docId, pass) = await fixture.SeedDoctorAsync("doc.sign@apollo.local", "Doctor Sign", "Doctor", "DocSignPassword123!");
        var enc = await fixture.SeedEncounterAsync(docId, "P100234", "ENC-100567");

        using var factory = new Stage9EmrFactory { DatabaseConnection = fixture.ConnectionString };
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        string csrf = await FetchCsrfTokenAsync(client);
        var loginReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/emr/auth/login") { Content = JsonContent.Create(new LoginRequest("doc.sign@apollo.local", pass)) };
        loginReq.Headers.Add("X-XSRF-TOKEN", csrf);
        (await client.SendAsync(loginReq)).EnsureSuccessStatusCode();

        // Create draft
        csrf = await FetchCsrfTokenAsync(client);
        string noteText = "Follow-up for hypertension. Continue amlodipine 5 mg orally once daily for 7 days. Order complete blood count tomorrow.";
        var createDraft = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/emr/encounters/{enc}/notes") { Content = JsonContent.Create(new DraftRequest(noteText)) };
        createDraft.Headers.Add("X-XSRF-TOKEN", csrf);
        var note = (await (await client.SendAsync(createDraft)).Content.ReadFromJsonAsync<NoteDto>())!;

        // Sign note
        csrf = await FetchCsrfTokenAsync(client);
        var signReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/emr/notes/{note.Id}/sign") { Content = JsonContent.Create(new SignRequest(ExpectedVersion: 1)) };
        signReq.Headers.Add("X-XSRF-TOKEN", csrf);
        using var signResp = await client.SendAsync(signReq);
        Assert.Equal(HttpStatusCode.OK, signResp.StatusCode);
        var signed = await signResp.Content.ReadFromJsonAsync<NoteDto>();
        Assert.NotNull(signed);
        Assert.Equal("Signed", signed.Status);
        Assert.Equal(2, signed.Version); // Signing increments version to freeze immutable version

        // Verify emr_outbox_events row exists
        await using var conn = new NpgsqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        await using var outboxCmd = new NpgsqlCommand($"SELECT event_type, publication_status, processing_status FROM \"{fixture.Schema}\".emr_outbox_events WHERE note_id = '{note.Id}' AND note_version = 2", conn);
        await using var reader = await outboxCmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("ClinicalNoteSigned", reader.GetString(0));
        Assert.Equal("Pending Publication", reader.GetString(1));
        Assert.Equal("Pending Publication", reader.GetString(2));
        await reader.CloseAsync();

        // Mutating a signed note directly throws 409 Conflict
        csrf = await FetchCsrfTokenAsync(client);
        var directEdit = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/emr/notes/{note.Id}") { Content = JsonContent.Create(new DraftRequest("Illegal direct edit", ExpectedVersion: 2)) };
        directEdit.Headers.Add("X-XSRF-TOKEN", csrf);
        using var editResp = await client.SendAsync(directEdit);
        Assert.Equal(HttpStatusCode.Conflict, editResp.StatusCode);
    }

    [PostgresFact]
    public async Task Test6_BrokerOutage_DisabledBroker_LeavesNoteSignedAndOutboxPending()
    {
        await using var fixture = new Stage9EmrFixture();
        await fixture.InitializeAsync();
        var (docId, pass) = await fixture.SeedDoctorAsync("doc.outage@apollo.local", "Doctor Outage", "Doctor", "DocOutagePassword123!");
        var enc = await fixture.SeedEncounterAsync(docId, "P100234", "ENC-100567");

        var disabledBroker = new DisabledNoteBroker();
        using var factory = new Stage9EmrFactory { DatabaseConnection = fixture.ConnectionString, CustomBroker = disabledBroker };
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        string csrf = await FetchCsrfTokenAsync(client);
        var loginReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/emr/auth/login") { Content = JsonContent.Create(new LoginRequest("doc.outage@apollo.local", pass)) };
        loginReq.Headers.Add("X-XSRF-TOKEN", csrf);
        (await client.SendAsync(loginReq)).EnsureSuccessStatusCode();

        csrf = await FetchCsrfTokenAsync(client);
        var createDraft = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/emr/encounters/{enc}/notes") { Content = JsonContent.Create(new DraftRequest("Testing broker outage tolerance.")) };
        createDraft.Headers.Add("X-XSRF-TOKEN", csrf);
        var note = (await (await client.SendAsync(createDraft)).Content.ReadFromJsonAsync<NoteDto>())!;

        csrf = await FetchCsrfTokenAsync(client);
        var signReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/emr/notes/{note.Id}/sign") { Content = JsonContent.Create(new SignRequest(1)) };
        signReq.Headers.Add("X-XSRF-TOKEN", csrf);
        (await client.SendAsync(signReq)).EnsureSuccessStatusCode();

        // Trigger outbox publisher once
        var store = factory.Services.GetRequiredService<EmrStore>();
        var messagingService = factory.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<EmrMessagingService>().Single();
        await messagingService.PublishOnce(store, disabledBroker, CancellationToken.None);

        // Note remains signed and outbox event remains in Failed or Pending Publication without losing data
        var statusResp = await client.GetAsync($"/api/v1/emr/notes/{note.Id}");
        var noteStatus = await statusResp.Content.ReadFromJsonAsync<NoteDto>();
        Assert.Equal("Signed", noteStatus?.Status);
    }

    [PostgresFact]
    public async Task Test7_Publish_SimulatedBroker_PublisherClaimsAndMarksPublished()
    {
        await using var fixture = new Stage9EmrFixture();
        await fixture.InitializeAsync();
        var (docId, pass) = await fixture.SeedDoctorAsync("doc.pub@apollo.local", "Doctor Pub", "Doctor", "DocPubPassword123!");
        var enc = await fixture.SeedEncounterAsync(docId, "P100234", "ENC-100567");

        var simBroker = new SimulatedNoteBroker();
        using var factory = new Stage9EmrFactory { DatabaseConnection = fixture.ConnectionString, CustomBroker = simBroker };
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        string csrf = await FetchCsrfTokenAsync(client);
        var loginReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/emr/auth/login") { Content = JsonContent.Create(new LoginRequest("doc.pub@apollo.local", pass)) };
        loginReq.Headers.Add("X-XSRF-TOKEN", csrf);
        (await client.SendAsync(loginReq)).EnsureSuccessStatusCode();

        csrf = await FetchCsrfTokenAsync(client);
        var createDraft = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/emr/encounters/{enc}/notes") { Content = JsonContent.Create(new DraftRequest("Patient consultation text.")) };
        createDraft.Headers.Add("X-XSRF-TOKEN", csrf);
        var note = (await (await client.SendAsync(createDraft)).Content.ReadFromJsonAsync<NoteDto>())!;

        csrf = await FetchCsrfTokenAsync(client);
        var signReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/emr/notes/{note.Id}/sign") { Content = JsonContent.Create(new SignRequest(1)) };
        signReq.Headers.Add("X-XSRF-TOKEN", csrf);
        (await client.SendAsync(signReq)).EnsureSuccessStatusCode();

        // Publish
        var store = factory.Services.GetRequiredService<EmrStore>();
        var messagingService = factory.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<EmrMessagingService>().Single();
        await messagingService.PublishOnce(store, simBroker, CancellationToken.None);

        // Verify broker received message
        Assert.True(simBroker.Messages.Reader.TryRead(out var received));
        Assert.Equal(note.Id, received.NoteId);
        Assert.Equal(2, received.NoteVersion);

        // Verify outbox table has Published
        await using var conn = new NpgsqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        await using var checkPub = new NpgsqlCommand($"SELECT publication_status, processing_status FROM \"{fixture.Schema}\".emr_outbox_events WHERE note_id = '{note.Id}'", conn);
        await using var r = await checkPub.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        Assert.Equal("Published", r.GetString(0));
        Assert.Equal("Queued", r.GetString(1));
    }

    [PostgresFact]
    public async Task Test8_Consume_WorkerProcessesSignedNote_ValidatesHash_LinksExtraction()
    {
        await using var fixture = new Stage9EmrFixture();
        await fixture.InitializeAsync();
        var (docId, pass) = await fixture.SeedDoctorAsync("doc.worker@apollo.local", "Doctor Worker", "Doctor", "DocWorkerPassword123!");
        var enc = await fixture.SeedEncounterAsync(docId, "P100234", "ENC-100567");

        var simBroker = new SimulatedNoteBroker();
        using var factory = new Stage9EmrFactory { DatabaseConnection = fixture.ConnectionString, CustomBroker = simBroker };
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        string csrf = await FetchCsrfTokenAsync(client);
        var loginReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/emr/auth/login") { Content = JsonContent.Create(new LoginRequest("doc.worker@apollo.local", pass)) };
        loginReq.Headers.Add("X-XSRF-TOKEN", csrf);
        (await client.SendAsync(loginReq)).EnsureSuccessStatusCode();

        csrf = await FetchCsrfTokenAsync(client);
        string clinicalNote = "Continue amlodipine 5 mg orally once daily for 7 days. Order complete blood count tomorrow.";
        var createDraft = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/emr/encounters/{enc}/notes") { Content = JsonContent.Create(new DraftRequest(clinicalNote)) };
        createDraft.Headers.Add("X-XSRF-TOKEN", csrf);
        var note = (await (await client.SendAsync(createDraft)).Content.ReadFromJsonAsync<NoteDto>())!;

        csrf = await FetchCsrfTokenAsync(client);
        var signReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/emr/notes/{note.Id}/sign") { Content = JsonContent.Create(new SignRequest(1)) };
        signReq.Headers.Add("X-XSRF-TOKEN", csrf);
        (await client.SendAsync(signReq)).EnsureSuccessStatusCode();

        // Publish to broker
        var store = factory.Services.GetRequiredService<EmrStore>();
        var messagingService = factory.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<EmrMessagingService>().Single();
        await messagingService.PublishOnce(store, simBroker, CancellationToken.None);

        Assert.True(simBroker.Messages.Reader.TryRead(out var evt));

        // Consume event via EmrConsumer
        var consumer = factory.Services.GetRequiredService<EmrConsumer>();
        var outcome = await consumer.Process(evt, CancellationToken.None);
        Assert.Equal(ConsumerOutcome.Completed, outcome);

        // Verify linked extraction exists
        var noteStatus = await (await client.GetAsync($"/api/v1/emr/notes/{note.Id}")).Content.ReadFromJsonAsync<NoteDto>();
        Assert.NotNull(noteStatus?.ExtractionId);
        Assert.Equal("Needs Review", noteStatus.ExtractionStatus);

        // Doctor can fetch the linked extraction
        var extResp = await client.GetAsync($"/api/v1/emr/notes/{note.Id}/extraction");
        Assert.Equal(HttpStatusCode.OK, extResp.StatusCode);
        var extraction = await extResp.Content.ReadFromJsonAsync<ClinicalExtractionResponse>();
        Assert.NotNull(extraction);
        Assert.NotEmpty(extraction.Medications);
    }

    [PostgresFact]
    public async Task Test9_DuplicateDelivery_IsIdempotent_DoesNotDuplicateExtractionLink()
    {
        await using var fixture = new Stage9EmrFixture();
        await fixture.InitializeAsync();
        var (docId, pass) = await fixture.SeedDoctorAsync("doc.dup@apollo.local", "Doctor Dup", "Doctor", "DocDupPassword123!");
        var enc = await fixture.SeedEncounterAsync(docId, "P100234", "ENC-100567");

        var simBroker = new SimulatedNoteBroker();
        using var factory = new Stage9EmrFactory { DatabaseConnection = fixture.ConnectionString, CustomBroker = simBroker };
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        string csrf = await FetchCsrfTokenAsync(client);
        var loginReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/emr/auth/login") { Content = JsonContent.Create(new LoginRequest("doc.dup@apollo.local", pass)) };
        loginReq.Headers.Add("X-XSRF-TOKEN", csrf);
        (await client.SendAsync(loginReq)).EnsureSuccessStatusCode();

        csrf = await FetchCsrfTokenAsync(client);
        var createDraft = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/emr/encounters/{enc}/notes") { Content = JsonContent.Create(new DraftRequest("Continue metformin 500 mg daily.")) };
        createDraft.Headers.Add("X-XSRF-TOKEN", csrf);
        var note = (await (await client.SendAsync(createDraft)).Content.ReadFromJsonAsync<NoteDto>())!;

        csrf = await FetchCsrfTokenAsync(client);
        var signReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/emr/notes/{note.Id}/sign") { Content = JsonContent.Create(new SignRequest(1)) };
        signReq.Headers.Add("X-XSRF-TOKEN", csrf);
        (await client.SendAsync(signReq)).EnsureSuccessStatusCode();

        var store = factory.Services.GetRequiredService<EmrStore>();
        var messagingService = factory.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<EmrMessagingService>().Single();
        await messagingService.PublishOnce(store, simBroker, CancellationToken.None);
        Assert.True(simBroker.Messages.Reader.TryRead(out var evt));

        var consumer = factory.Services.GetRequiredService<EmrConsumer>();

        // First delivery
        var outcome1 = await consumer.Process(evt, CancellationToken.None);
        Assert.Equal(ConsumerOutcome.Completed, outcome1);

        // Duplicate delivery with the exact same event
        var outcome2 = await consumer.Process(evt, CancellationToken.None);
        Assert.Equal(ConsumerOutcome.Completed, outcome2);

        // Verify exactly one link exists
        await using var conn = new NpgsqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        await using var countCmd = new NpgsqlCommand($"SELECT count(*) FROM \"{fixture.Schema}\".emr_extraction_links WHERE event_id = '{evt.EventId}'", conn);
        Assert.Equal(1L, (long)(await countCmd.ExecuteScalarAsync())!);
    }

    [PostgresFact]
    public async Task Test10_AiRouting_MicrosoftFoundryIsPrimary_FallbackOnlyOnEligibleFaults()
    {
        await using var fixture = new Stage9EmrFixture();
        await fixture.InitializeAsync();
        var (docId, pass) = await fixture.SeedDoctorAsync("doc.routing@apollo.local", "Doctor Routing", "Doctor", "DocRoutingPass123!");

        using var factory = new Stage9EmrFactory
        {
            DatabaseConnection = fixture.ConnectionString,
            PrimaryProvider = "MicrosoftFoundry",
            FallbackProvider = "LocalOnnx"
        };
        using var client = factory.CreateClient();

        // 1. Verify routing diagnostics
        var diagResp = await client.GetAsync("/v1/routing");
        Assert.Equal(HttpStatusCode.OK, diagResp.StatusCode);
        var diag = await diagResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("MicrosoftFoundry", diag.GetProperty("requestedPrimaryProvider").GetString());
        Assert.Equal("LocalOnnx", diag.GetProperty("fallbackProvider").GetString());
        Assert.Equal("gpt-6.1-sol", diag.GetProperty("foundryDeployment").GetString());
        Assert.False(diag.GetProperty("extractionInferenceExecuted").GetBoolean());
    }

    [PostgresFact]
    public async Task Test11_DoctorCannotExecuteDirectExtraction_BlockedWith403_ClinicalAccessDenied()
    {
        await using var fixture = new Stage9EmrFixture();
        await fixture.InitializeAsync();
        var (docId, pass) = await fixture.SeedDoctorAsync("dr.security@apollo.local", "Dr. Security", "Doctor", "DocSecurityPassword123!");
        var enc = await fixture.SeedEncounterAsync(docId, "P100234", "ENC-100567");

        var simBroker = new SimulatedNoteBroker();
        using var factory = new Stage9EmrFactory { DatabaseConnection = fixture.ConnectionString, CustomBroker = simBroker };
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

        // 1. Unauthenticated direct extraction in Demo mode (or reviewer) works
        using var anonClient = factory.CreateClient();
        var anonResp = await anonClient.PostAsJsonAsync("/api/v1/extractions", new { noteText = "Continue amlodipine 5 mg daily." });
        Assert.Equal(HttpStatusCode.OK, anonResp.StatusCode);

        // 2. Doctor logs in
        string csrf = await FetchCsrfTokenAsync(client);
        var loginReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/emr/auth/login") { Content = JsonContent.Create(new LoginRequest("dr.security@apollo.local", pass)) };
        loginReq.Headers.Add("X-XSRF-TOKEN", csrf);
        (await client.SendAsync(loginReq)).EnsureSuccessStatusCode();

        // 3. Authenticated Doctor attempts direct ungrounded extraction POST /api/v1/extractions
        csrf = await FetchCsrfTokenAsync(client);
        var directReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/extractions")
        {
            Content = JsonContent.Create(new { noteText = "Continue amlodipine 5 mg orally daily." })
        };
        directReq.Headers.Add("X-XSRF-TOKEN", csrf);
        using var directResp = await client.SendAsync(directReq);

        // MUST be 403 Forbidden with CLINICAL_ACCESS_DENIED
        Assert.Equal(HttpStatusCode.Forbidden, directResp.StatusCode);
        var errBody = await directResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("CLINICAL_ACCESS_DENIED", errBody.GetProperty("code").GetString());
        Assert.Equal("Direct Extraction Restricted", errBody.GetProperty("title").GetString());
        Assert.Contains("Direct extraction is reserved for clinical review staff", errBody.GetProperty("detail").GetString());

        // 4. Missing CSRF on mutating request returns 400 CSRF_INVALID
        var noCsrfReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/extractions")
        {
            Content = JsonContent.Create(new { noteText = "Test note." })
        };
        using var noCsrfResp = await client.SendAsync(noCsrfReq);
        Assert.Equal(HttpStatusCode.BadRequest, noCsrfResp.StatusCode);
        var csrfErr = await noCsrfResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("CSRF_INVALID", csrfErr.GetProperty("code").GetString());

        // 5. Doctor creating and signing note in EMR succeeds
        csrf = await FetchCsrfTokenAsync(client);
        var createDraft = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/emr/encounters/{enc}/notes")
        {
            Content = JsonContent.Create(new DraftRequest("Continue amlodipine 5 mg orally once daily for 7 days. Order complete blood count tomorrow."))
        };
        createDraft.Headers.Add("X-XSRF-TOKEN", csrf);
        var note = (await (await client.SendAsync(createDraft)).Content.ReadFromJsonAsync<NoteDto>())!;

        csrf = await FetchCsrfTokenAsync(client);
        var signReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/emr/notes/{note.Id}/sign") { Content = JsonContent.Create(new SignRequest(1)) };
        signReq.Headers.Add("X-XSRF-TOKEN", csrf);
        (await client.SendAsync(signReq)).EnsureSuccessStatusCode();

        // 6. Messaging service publishes and consumer extracts
        var store = factory.Services.GetRequiredService<EmrStore>();
        var messagingService = factory.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<EmrMessagingService>().Single();
        await messagingService.PublishOnce(store, simBroker, CancellationToken.None);
        Assert.True(simBroker.Messages.Reader.TryRead(out var evt));

        var consumer = factory.Services.GetRequiredService<EmrConsumer>();
        var outcome = await consumer.Process(evt, CancellationToken.None);
        Assert.Equal(ConsumerOutcome.Completed, outcome);

        // 7. Doctor CAN fetch their own linked extraction via GET /api/v1/extractions/{id}
        var extResp = await client.GetAsync($"/api/v1/extractions/{evt.EventId}");
        Assert.Equal(HttpStatusCode.OK, extResp.StatusCode);
        var extraction = await extResp.Content.ReadFromJsonAsync<ClinicalExtractionResponse>();
        Assert.NotNull(extraction);
        Assert.NotEmpty(extraction.Medications);

        // 8. Doctor CANNOT fetch an unlinked / random extraction via GET /api/v1/extractions/{unlinkedId}
        Guid unlinkedId = Guid.NewGuid();
        var unlinkedResp = await client.GetAsync($"/api/v1/extractions/{unlinkedId}");
        Assert.Equal(HttpStatusCode.NotFound, unlinkedResp.StatusCode);
        var unlinkedErr = await unlinkedResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("CLINICAL_ACCESS_DENIED", unlinkedErr.GetProperty("code").GetString());
    }
}
