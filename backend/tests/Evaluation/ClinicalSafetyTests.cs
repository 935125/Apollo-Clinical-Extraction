using System.Net.Http.Json;
using Apollo.Backend.Clinical;
using Apollo.Backend.Models;
using Apollo.Backend.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Apollo.Backend.Tests.Evaluation;

public sealed class ClinicalSafetyTests
{
    private static WebApplicationFactory<Program> CreateFoundryFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IExtractionRepository>();
                services.AddSingleton<IExtractionRepository, InMemoryExtractionRepository>();
                services.PostConfigure<ExtractionOptions>(o =>
                {
                    o.Provider = "MicrosoftFoundry";
                    o.FallbackProvider = "None";
                });
            });
        });

    [FoundryLiveFact]
    public async Task Safety_MethotrexateWeekly_NeverExtractedAsDaily()
    {
        using var factory = CreateFoundryFactory();
        using var client = factory.CreateClient();

        const string note = "Rheumatoid arthritis flare. Prescribe Methotrexate 15mg PO once weekly on Sundays. Warn patient never to take daily.";
        using var response = await client.PostAsJsonAsync("/api/v1/extractions", new ClinicalExtractionRequest(note));
        response.EnsureSuccessStatusCode();

        var result = (await response.Content.ReadFromJsonAsync<ClinicalExtractionResponse>())!;
        var mtx = result.Medications.FirstOrDefault(m => m.Name.Contains("methotrexate", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(mtx);
        Assert.NotNull(mtx.Frequency);
        Assert.Contains("weekly", mtx.Frequency, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("daily", mtx.Frequency, StringComparison.OrdinalIgnoreCase);
    }

    [FoundryLiveFact]
    public async Task Safety_PediatricWeightBasedDosing_PreservesExactDoseAndWeightContext()
    {
        using var factory = CreateFoundryFactory();
        using var client = factory.CreateClient();

        const string note = "Child weighing 20kg. Prescribe Amoxicillin 800mg PO BD for 10 days (high dose 80mg/kg/day).";
        using var response = await client.PostAsJsonAsync("/api/v1/extractions", new ClinicalExtractionRequest(note));
        response.EnsureSuccessStatusCode();

        var result = (await response.Content.ReadFromJsonAsync<ClinicalExtractionResponse>())!;
        var amox = result.Medications.FirstOrDefault(m => m.Name.Contains("amoxicillin", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(amox);
        Assert.Null(amox.Strength);
        Assert.Null(amox.Dose);
        Assert.Contains("80mg/kg/day", amox.Instructions);
        Assert.Contains("COMPLEX_REGIMEN_REVIEW", amox.QualityFlags);
    }

    [FoundryLiveFact]
    public async Task Safety_WarfarinAndInr_CorrelatesAnticoagulantWithMonitoring()
    {
        using var factory = CreateFoundryFactory();
        using var client = factory.CreateClient();

        const string note = "Patient on Warfarin 5mg daily. Check INR today before adjusting dose. If INR > 3.5, hold Warfarin.";
        using var response = await client.PostAsJsonAsync("/api/v1/extractions", new ClinicalExtractionRequest(note));
        response.EnsureSuccessStatusCode();

        var result = (await response.Content.ReadFromJsonAsync<ClinicalExtractionResponse>())!;
        Assert.Contains(result.Medications, m => m.Name.Contains("warfarin", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Investigations, i => i.Name.Contains("inr", StringComparison.OrdinalIgnoreCase));
    }

    [FoundryLiveFact]
    public async Task Safety_NegatedPenicillinAllergy_IsNotRecordedAsConfirmedAllergy()
    {
        using var factory = CreateFoundryFactory();
        using var client = factory.CreateClient();

        const string note = "Patient has seasonal allergic rhinitis. Patient denies penicillin allergy. Start amoxicillin 500mg TDS.";
        using var response = await client.PostAsJsonAsync("/api/v1/extractions", new ClinicalExtractionRequest(note));
        response.EnsureSuccessStatusCode();

        var result = (await response.Content.ReadFromJsonAsync<ClinicalExtractionResponse>())!;
        var amox = result.Medications.FirstOrDefault(m => m.Name.Contains("amoxicillin", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(amox);

        var penicillin = result.Medications.FirstOrDefault(m => m.Name.Contains("penicillin", StringComparison.OrdinalIgnoreCase));
        if (penicillin != null)
        {
            Assert.True(penicillin.AssertionStatus == "negated" && penicillin.Action == "none",
                $"Penicillin was marked as active allergy: {penicillin.AssertionStatus}");
        }
    }

    [FoundryLiveFact]
    public async Task Safety_ConditionalChestXRay_RemainsConditional()
    {
        using var factory = CreateFoundryFactory();
        using var client = factory.CreateClient();

        const string note = "Mild upper respiratory tract symptoms. Order Complete Blood Count. Consider chest X-ray only if cough persists.";
        using var response = await client.PostAsJsonAsync("/api/v1/extractions", new ClinicalExtractionRequest(note));
        response.EnsureSuccessStatusCode();

        var result = (await response.Content.ReadFromJsonAsync<ClinicalExtractionResponse>())!;
        var cxr = result.Investigations.FirstOrDefault(i => i.Name.Contains("chest", StringComparison.OrdinalIgnoreCase) || i.Name.Contains("x-ray", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(cxr);
        Assert.Equal("conditional", cxr.AssertionStatus);
    }
}

