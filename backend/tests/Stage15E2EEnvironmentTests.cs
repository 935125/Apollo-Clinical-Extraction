using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Apollo.Backend.Emr;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Apollo.Backend.Tests;

public sealed class Stage15SetupFactAttribute : FactAttribute
{
    public Stage15SetupFactAttribute()
    {
        if(Environment.GetEnvironmentVariable("APOLLO_STAGE15_PREPARE_E2E")!="1") Skip="Explicit isolated browser/Postman fixture setup only";
    }
}
public class Stage15E2EEnvironmentTests
{
    [Stage15SetupFact]
    public async Task CreateIsolatedRetainedSyntheticFixture()
    {
        // Deliberately retained for external CLI/browser verification. It is a newly created
        // schema in cx_audit_test, never the application database or an existing schema.
        var fixture=new Stage9EmrFixture(); await fixture.InitializeAsync();
        var (doctor,_)=await fixture.SeedDoctorAsync("doctor.krishnan@apollo.local","Dr. Stage15 Synthetic",password:Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        var encounters=new List<Guid>();
        for(int i=0;i<4;i++) encounters.Add(await fixture.SeedEncounterAsync(doctor,$"P{100234+i}",$"ENC-{100567+i}"));
        using var factory=new Stage9EmrFactory{DatabaseConnection=fixture.ConnectionString,PrimaryProvider="LocalOnnx",DisableMessagingService=true};
        using var client=factory.CreateClient(new WebApplicationFactoryClientOptions{HandleCookies=true});
        (await client.PostAsync("/api/v1/emr/auth/dev-session",null)).EnsureSuccessStatusCode();
        async Task<T> Post<T>(string url,object body)
        {
            var csrf=(await client.GetFromJsonAsync<JsonElement>("/api/v1/emr/auth/csrf")).GetProperty("token").GetString()!;
            using var req=new HttpRequestMessage(HttpMethod.Post,url){Content=JsonContent.Create(body)};req.Headers.Add("X-XSRF-TOKEN",csrf);
            using var response=await client.SendAsync(req);response.EnsureSuccessStatusCode();return (await response.Content.ReadFromJsonAsync<T>())!;
        }
        foreach(var encounter in encounters)
        {
            var draft=await Post<NoteDto>($"/api/v1/emr/encounters/{encounter}/notes",new DraftRequest("Stage 15 isolated synthetic baseline. Continue Aspirin 75 mg, 1 tablet PO daily."));
            await Post<NoteDto>($"/api/v1/emr/notes/{draft.Id}/sign",new SignRequest(draft.Version));
        }
        var dir=Path.Combine(Evaluation.Stage15BenchmarkTests.Root,"docs/stage15-evidence/integration");Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir,"isolated-environment.json"),JsonSerializer.Serialize(new{
            database="cx_audit_test",schema=fixture.Schema,connectionString=fixture.ConnectionString,baseUrl="http://127.0.0.1:5015",
            liveFoundryCalls=0,synthetic=true,applicationDatabaseTargeted=false},new JsonSerializerOptions(JsonSerializerDefaults.Web){WriteIndented=true}));
    }
}
