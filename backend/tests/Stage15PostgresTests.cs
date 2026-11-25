using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Apollo.Backend.Clinical;
using Apollo.Backend.Emr;
using Apollo.Backend.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Apollo.Backend.Storage;

namespace Apollo.Backend.Tests;

public class Stage15PostgresTests
{
    private sealed class ProhibitFoundry : IFoundryClinicalExtractor
    {
        public Task<FoundryExtraction> ExtractAsync(string source,CancellationToken ct)=>throw new InvalidOperationException("Live Foundry prohibited in Stage 15 tests");
    }
    [PostgresFact]
    public async Task RealLocalProvider_PreservesAllStructuredProvenanceAndNormalizationOnDatabaseReload()
    {
        await using var database=new IsolatedDatabase(); await database.Initialize();
        using var original=new FoundryFactory{DatabaseConnection=database.ConnectionString,PrimaryProvider="LocalOnnx"};
        using var factory=original.WithWebHostBuilder(builder=>builder.ConfigureServices(s=>s.PostConfigure<LocalClinicalPipelineOptions>(o=>o.PipelineVersion="CandidateV2")));
        using var client=factory.CreateClient();
        const string source="Chemotherapy Regimen:\nCytarabine 100 mg/m2 continuous IV infusion + Daunorubicin 60 mg/m2 IV push.\nDischarge Medications:\nAcyclovir 400 mg PO BD.\nFollow-Up Plan:\nReport on 01/12/2026 at 09:00 AM for CBC and renal function panel.";
        var watch=Stopwatch.StartNew();
        using var response=await client.PostAsJsonAsync("/api/v1/extractions",new ClinicalExtractionRequest(source));
        Assert.True(response.IsSuccessStatusCode,await response.Content.ReadAsStringAsync());
        var saved=(await response.Content.ReadFromJsonAsync<ClinicalExtractionResponse>())!;
        var restored=(await client.GetFromJsonAsync<ClinicalExtractionResponse>("/api/v1/extractions/"+saved.ExtractionId))!;
        double apiAndReloadMs=watch.Elapsed.TotalMilliseconds;
        Assert.Equal(0,original.Azure.Calls);
        Assert.Equal("CandidateV2",restored.ModelMetadata.LocalPipelineVersion);
        Assert.Equal("LocalOnnx",restored.ModelMetadata.FinalExecutedProvider);
        Assert.Equal("LocalOnnx",restored.ModelMetadata.RequestedPrimaryProvider);
        Assert.False(restored.ModelMetadata.FallbackTriggered);
        Assert.Equal(3,restored.Medications.Count); Assert.Equal(2,restored.Investigations.Count);
        foreach(var m in saved.Medications)
        {
            var reloaded=Assert.Single(restored.Medications,x=>x.Provenance?.ClinicalContext?.SourceStart==m.Provenance?.ClinicalContext?.SourceStart);
            Assert.Equal(JsonSerializer.Serialize(m),JsonSerializer.Serialize(reloaded));
        }
        foreach(var i in saved.Investigations)
        {
            var reloaded=Assert.Single(restored.Investigations,x=>x.Provenance?.ClinicalContext?.SourceStart==i.Provenance?.ClinicalContext?.SourceStart);
            Assert.Equal(JsonSerializer.Serialize(i),JsonSerializer.Serialize(reloaded));
        }
        LocalEvidenceValidator.Validate(restored,source);
        Assert.All(restored.Investigations,i=>Assert.Equal("scheduled",i.Provenance!.ClinicalContext!.Assertion));
        Assert.Equal(1,await database.Count("extraction_runs"));
        var repository=factory.Services.GetRequiredService<IExtractionRepository>();
        string probeId=Guid.NewGuid().ToString("D");
        watch.Restart(); await repository.SaveExtractionAsync(probeId,saved with{ExtractionId=probeId});
        double persistenceOnlyMs=watch.Elapsed.TotalMilliseconds;
        watch.Restart(); Assert.NotNull(await repository.GetExtractionAsync(probeId));
        double reloadOnlyMs=watch.Elapsed.TotalMilliseconds;
        var dir=Path.Combine(Evaluation.Stage15BenchmarkTests.Root,"docs/stage15-evidence/integration"); Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir,"real-postgres-roundtrip.json"),JsonSerializer.Serialize(new{
            database="cx_audit_test",apiAndReloadMs,persistenceOnlyMs,reloadOnlyMs,liveFoundryCalls=0,result=restored},new JsonSerializerOptions(JsonSerializerDefaults.Web){WriteIndented=true}));
    }

    [PostgresFact]
    public async Task SignedSyntheticVersions_RealOnnx_MockedMessaging_PreservePatientEncounterAndExtractionIdentity()
    {
        await using var fixture=new Stage9EmrFixture(); await fixture.InitializeAsync();
        var (doctorA,passwordA)=await fixture.SeedDoctorAsync("stage15.a@apollo.local",password:"Stage15Password123!");
        var (doctorB,passwordB)=await fixture.SeedDoctorAsync("stage15.b@apollo.local",password:"Stage15Password123!");
        var encounterA=await fixture.SeedEncounterAsync(doctorA,"P950001","ENC-950001");
        var encounterB=await fixture.SeedEncounterAsync(doctorB,"P950002","ENC-950002");
        var broker=new SimulatedNoteBroker();
        using var original=new Stage9EmrFactory{DatabaseConnection=fixture.ConnectionString,CustomBroker=broker,PrimaryProvider="LocalOnnx"};
        using var factory=original.WithWebHostBuilder(builder=>builder.ConfigureServices(s=>{
            s.PostConfigure<LocalClinicalPipelineOptions>(o=>o.PipelineVersion="CandidateV2");
            s.RemoveAll<IFoundryClinicalExtractor>();s.AddSingleton<IFoundryClinicalExtractor,ProhibitFoundry>();
        }));
        using var clientA=factory.CreateClient(new WebApplicationFactoryClientOptions{HandleCookies=true});
        using var clientB=factory.CreateClient(new WebApplicationFactoryClientOptions{HandleCookies=true});
        async Task<string> Csrf(HttpClient client)=>(await client.GetFromJsonAsync<JsonElement>("/api/v1/emr/auth/csrf")).GetProperty("token").GetString()!;
        async Task<T> Post<T>(HttpClient client,string path,object body)
        {
            using var req=new HttpRequestMessage(HttpMethod.Post,path){Content=JsonContent.Create(body)};req.Headers.Add("X-XSRF-TOKEN",await Csrf(client));
            using var response=await client.SendAsync(req);Assert.True(response.IsSuccessStatusCode,await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<T>())!;
        }
        await Post<DoctorProfile>(clientA,"/api/v1/emr/auth/login",new LoginRequest("stage15.a@apollo.local",passwordA));
        await Post<DoctorProfile>(clientB,"/api/v1/emr/auth/login",new LoginRequest("stage15.b@apollo.local",passwordB));
        async Task<(NoteDto,ClinicalNoteSigned)> Sign(HttpClient client,Guid encounter,string text,NoteDto? previous=null)
        {
            var draft=await Post<NoteDto>(client,previous==null ? $"/api/v1/emr/encounters/{encounter}/notes" : $"/api/v1/emr/notes/{previous.Id}/corrections",new DraftRequest(text,previous?.Version ?? 0));
            var signed=await Post<NoteDto>(client,$"/api/v1/emr/notes/{draft.Id}/sign",new SignRequest(draft.Version));
            var store=factory.Services.GetRequiredService<EmrStore>();
            var publisher=factory.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<EmrMessagingService>().Single();
            await publisher.PublishOnce(store,broker,CancellationToken.None);
            Assert.True(broker.Messages.Reader.TryRead(out var evt));
            var consumer=factory.Services.GetRequiredService<EmrConsumer>();
            Assert.Equal(ConsumerOutcome.Completed,await consumer.Process(evt,CancellationToken.None));
            Assert.Equal(ConsumerOutcome.Completed,await consumer.Process(evt,CancellationToken.None));
            return(signed,evt);
        }
        var (a1,e1)=await Sign(clientA,encounterA,"Allergy History:\nAspirin.\nFollow-Up Plan:\nReport on 01/12/2026 for CBC.");
        var (a2,e2)=await Sign(clientA,encounterA,"Discharge Medications:\nAcyclovir 400 mg PO twice daily.",a1);
        var (b1,e3)=await Sign(clientB,encounterB,"Stop Warfarin 5 mg.");
        var first=(await clientA.GetFromJsonAsync<QueueEntryDetailDto>($"/api/v1/queue/{a1.Id}?version={a1.Version}&patient=P950001"))!;
        var second=(await clientA.GetFromJsonAsync<QueueEntryDetailDto>($"/api/v1/queue/{a2.Id}?version={a2.Version}&patient=P950001"))!;
        var other=(await clientB.GetFromJsonAsync<QueueEntryDetailDto>($"/api/v1/queue/{b1.Id}?version={b1.Version}&patient=P950002"))!;
        Assert.Equal("allergy",Assert.Single(first.Extraction!.Medications).AssertionStatus);
        Assert.Equal("Acyclovir",Assert.Single(second.Extraction!.Medications).Name);
        Assert.Equal("discontinued",Assert.Single(other.Extraction!.Medications).AssertionStatus);
        Assert.Equal(e1.EventId,first.ExtractionId);Assert.Equal(e2.EventId,second.ExtractionId);Assert.Equal(e3.EventId,other.ExtractionId);
        Assert.Equal(3,await fixture.Database.Count("extraction_runs"));
        Assert.Equal(HttpStatusCode.NotFound,(await clientA.GetAsync($"/api/v1/queue/{b1.Id}?patient=P950002")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await clientA.GetAsync($"/api/v1/queue/{a1.Id}?patient=P950002")).StatusCode);
    }
}
