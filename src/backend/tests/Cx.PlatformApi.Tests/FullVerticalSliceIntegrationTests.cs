using System.Net;
using System.Net.Http.Json;
using Cx.PlatformApi.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;
using Xunit;

namespace Cx.PlatformApi.Tests;

public class FullVerticalSliceIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public FullVerticalSliceIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task VerticalSlice_RealLiveRoundTrip_DotNetToPython_ExtractsClinicalEntities()
    {
        // Tests real live communication between ASP.NET Core Web API and Internal Python AI service on 127.0.0.1:5005
        var client = _factory.CreateClient();
        var correlationId = "slice-" + Guid.NewGuid().ToString("N");
        client.DefaultRequestHeaders.Add("X-Correlation-ID", correlationId);

        var clinicalNote = """
            Chief Complaint: Follow up for hypertension and type 2 diabetes.
            Plan:
            1. Prescribe Metformin 500mg PO BD with meals.
            2. Prescribe Atorvastatin 20mg PO at bedtime.
            3. Order Serum Creatinine and HbA1c.
            """;

        var request = new ClinicalExtractionRequest(NoteText: clinicalNote);

        var response = await client.PostAsJsonAsync("/api/v1/extractions", request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.Contains("X-Correlation-ID").ShouldBeTrue();
        response.Headers.GetValues("X-Correlation-ID").First().ShouldBe(correlationId);

        var result = await response.Content.ReadFromJsonAsync<ClinicalExtractionResponse>();
        result.ShouldNotBeNull();
        result.CorrelationId.ShouldBe(correlationId);
        result.ExecutionDurationMs.ShouldBeGreaterThan(0);
        result.Preprocessed.CharLength.ShouldBeGreaterThan(50);

        // Verify extracted medications
        result.Medications.Count.ShouldBeGreaterThanOrEqualTo(2);
        var metformin = result.Medications.FirstOrDefault(m => m.Name == "Metformin");
        metformin.ShouldNotBeNull();
        metformin.Strength.ShouldBe("500mg");
        metformin.Route.ShouldBe("PO");
        metformin.Frequency.ShouldBe("BD");
        metformin.AssertionStatus.ShouldBe("new_order");
        metformin.Evidence.Verified.ShouldBeTrue();
        metformin.Normalization.Status.ShouldBe("exact_match");
        metformin.Normalization.MatchedCode.ShouldBe("RX-MET-500");

        var atorvastatin = result.Medications.FirstOrDefault(m => m.Name == "Atorvastatin");
        atorvastatin.ShouldNotBeNull();
        atorvastatin.Strength.ShouldBe("20mg");
        atorvastatin.Evidence.Verified.ShouldBeTrue();
        atorvastatin.Normalization.Status.ShouldBe("exact_match");
        atorvastatin.Normalization.MatchedCode.ShouldBe("RX-ATO-20");

        // Verify extracted investigations
        result.Investigations.Count.ShouldBeGreaterThanOrEqualTo(2);
        var creat = result.Investigations.FirstOrDefault(i => i.Name == "Serum Creatinine");
        creat.ShouldNotBeNull();
        creat.Normalization.Status.ShouldBe("exact_match");
        creat.Normalization.MatchedCode.ShouldBe("LAB-CREAT");

        // Verify validation summary
        result.ValidationSummary.HasErrors.ShouldBeFalse();
    }
}

