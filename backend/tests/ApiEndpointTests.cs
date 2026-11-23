using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Apollo.Backend.Models;
using Apollo.Backend.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Apollo.Backend.Tests;

public sealed class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IExtractionRepository>();
            services.AddSingleton<IExtractionRepository, InMemoryExtractionRepository>();
            services.PostConfigure<Apollo.Backend.Clinical.ExtractionOptions>(o => o.Provider = "LocalOnnx");
        });
    }
}

public class ApiEndpointTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client;

    public ApiEndpointTests(TestWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task HealthLive_ReturnsOk()
    {
        var response = await _client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string content = await response.Content.ReadAsStringAsync();
        Assert.Contains("Healthy", content);
    }

    [Fact]
    public async Task HealthReady_ReturnsOk()
    {
        var response = await _client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string content = await response.Content.ReadAsStringAsync();
        Assert.Contains("Healthy", content);
    }

    [Fact]
    public async Task V1Health_ReturnsOk()
    {
        var response = await _client.GetAsync("/v1/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ApiV1Ping_ReturnsActive()
    {
        var response = await _client.GetAsync("/api/v1/ping");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string content = await response.Content.ReadAsStringAsync();
        Assert.Contains("active", content);
    }

    [Fact]
    public async Task Extractions_Rejects_EmptyNote()
    {
        var req = new ClinicalExtractionRequest("");
        var response = await _client.PostAsJsonAsync("/api/v1/extractions", req);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Extractions_Rejects_OversizedNote()
    {
        string giantNote = new string('A', 66000);
        var req = new ClinicalExtractionRequest(giantNote);
        var response = await _client.PostAsJsonAsync("/api/v1/extractions", req);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TextAndTextPdf_UseSameSavedWorkflow_AndProduceEquivalentClinicalResults()
    {
        const string note = "Plan: Start metformin 500 mg 1 tablet PO BID. Order CBC and CRP.";

        var textResponse = await _client.PostAsJsonAsync(
            "/api/v1/extractions", new ClinicalExtractionRequest(note));
        textResponse.EnsureSuccessStatusCode();
        var fromText = await textResponse.Content.ReadFromJsonAsync<ClinicalExtractionResponse>();
        Assert.NotNull(fromText);

        using var form = new MultipartFormDataContent();
        using var pdf = new ByteArrayContent(CreateTextPdf(note));
        pdf.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(pdf, "file", "fictional-note.pdf");
        var pdfResponse = await _client.PostAsync("/api/v1/extractions/pdf", form);
        pdfResponse.EnsureSuccessStatusCode();
        var fromPdf = await pdfResponse.Content.ReadFromJsonAsync<ClinicalExtractionResponse>();
        Assert.NotNull(fromPdf);

        Assert.True(fromText.Saved);
        Assert.True(fromPdf.Saved);
        Assert.Equal("text", fromText.InputType);
        Assert.Equal("pdf", fromPdf.InputType);
        Assert.Equal("fictional-note.pdf", fromPdf.SourceFileName);
        Assert.Equal(JsonSerializer.Serialize(fromText.Medications), JsonSerializer.Serialize(fromPdf.Medications));
        Assert.Equal(JsonSerializer.Serialize(fromText.Investigations), JsonSerializer.Serialize(fromPdf.Investigations));

        var reopenedText = await _client.GetFromJsonAsync<ClinicalExtractionResponse>(
            $"/api/v1/extractions/{fromText.ExtractionId}");
        var reopenedPdf = await _client.GetFromJsonAsync<ClinicalExtractionResponse>(
            $"/api/v1/extractions/{fromPdf.ExtractionId}");
        Assert.Equal(fromText.Medications.Select(m => m.Evidence), reopenedText!.Medications.Select(m => m.Evidence));
        Assert.Equal(fromPdf.Investigations.Select(i => i.Evidence), reopenedPdf!.Investigations.Select(i => i.Evidence));
    }

    [Fact]
    public async Task TextlessPdf_ReturnsClearOcrRequiredError()
    {
        using var form = new MultipartFormDataContent();
        using var pdf = new ByteArrayContent(CreateTextPdf(null));
        pdf.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(pdf, "file", "scan.pdf");

        var response = await _client.PostAsync("/api/v1/extractions/pdf", form);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("OCR_REQUIRED", await response.Content.ReadAsStringAsync());
    }

    private static byte[] CreateTextPdf(string? text)
    {
        string escaped = (text ?? string.Empty).Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
        string content = text == null ? "q Q" : $"BT /F1 12 Tf 72 720 Td ({escaped}) Tj ET";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream"
        ];

        var output = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int> { 0 };
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(output.ToString()));
            output.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        int xref = Encoding.ASCII.GetByteCount(output.ToString());
        output.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets.Skip(1))
            output.Append($"{offset:0000000000} 00000 n \n");
        output.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF");
        return Encoding.ASCII.GetBytes(output.ToString());
    }

    [Fact]
    public async Task Extractions_Blocks_PII_With_422()
    {
        string piiNote = "Patient contact phone: +91 9876543210. Prescribed Metformin 500 mg.";
        var req = new ClinicalExtractionRequest(piiNote);
        var response = await _client.PostAsJsonAsync("/api/v1/extractions", req);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        string content = await response.Content.ReadAsStringAsync();
        Assert.Contains("PII_LEAKAGE_DETECTED", content);
        // Zero raw PII leaked in response!
        Assert.DoesNotContain("9876543210", content);
    }

    [Fact]
    public async Task Extractions_Succeeds_On_Valid_Note()
    {
        string validNote = "Patient with Type 2 Diabetes. Prescribed Metformin 500 mg PO twice daily. Ordered Complete Blood Count.";
        var req = new ClinicalExtractionRequest(validNote);
        var response = await _client.PostAsJsonAsync("/api/v1/extractions", req);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<ClinicalExtractionResponse>();
        Assert.NotNull(result);
        Assert.NotEmpty(result.ExtractionId);
        Assert.True(result.Preprocessed.CharLength > 0);
    }

    [Fact]
    public async Task NerPredict_Blocks_PII_With_422()
    {
        string piiText = "Patient Aadhaar: 1234 5678 9012. Needs prescription refill.";
        var req = new ClinicalNERRequest(piiText);
        var response = await _client.PostAsJsonAsync("/v1/ner/predict", req);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        string content = await response.Content.ReadAsStringAsync();
        Assert.Contains("PII/PHI detected", content);
        Assert.DoesNotContain("1234 5678 9012", content);
    }

    [Fact]
    public async Task NerPredict_Succeeds_With_Full_Extraction()
    {
        string text = "Patient has Hypertension. Taking Atorva 20 mg daily. Ordered CUE and Fasting Lipid Profile.";
        var req = new ClinicalNERRequest(text, ApplyAssertion: true, EnableGrounding: true);
        var response = await _client.PostAsJsonAsync("/v1/ner/predict", req);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<ClinicalNERResponse>();
        Assert.NotNull(result);
        Assert.NotNull(result.MicroCard);
        Assert.NotNull(result.FhirBundle);
        Assert.NotEmpty(result.Entities);
    }

    [Fact]
    public async Task PredictPdf_Rejects_Files_Over_20MiB_With_413()
    {
        using var content = new MultipartFormDataContent();
        // Create dummy stream exceeding 20 MiB
        byte[] largeBytes = new byte[21 * 1024 * 1024];
        var byteContent = new ByteArrayContent(largeBytes);
        byteContent.Headers.ContentType = MediaTypeHeaderValue.Parse("application/pdf");
        content.Add(byteContent, "file", "giant_clinical_report.pdf");

        var response = await _client.PostAsync("/predict/pdf", content);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Extractions_GetList_And_GetById_RetrievePersistedRun()
    {
        string note = "Plan: Start amoxicillin 125 mg PO BID for 7 days. Investigations: CBC and Chest X-ray.";
        var postResp = await _client.PostAsJsonAsync("/api/v1/extractions", new ClinicalExtractionRequest(note));
        Assert.Equal(HttpStatusCode.OK, postResp.StatusCode);

        var created = await postResp.Content.ReadFromJsonAsync<ClinicalExtractionResponse>();
        Assert.NotNull(created);
        Assert.NotEmpty(created.ExtractionId);

        // 1. GET /api/v1/extractions/{id}
        var getByIdResp = await _client.GetAsync($"/api/v1/extractions/{created.ExtractionId}");
        Assert.Equal(HttpStatusCode.OK, getByIdResp.StatusCode);

        var retrieved = await getByIdResp.Content.ReadFromJsonAsync<ClinicalExtractionResponse>();
        Assert.NotNull(retrieved);
        Assert.Equal(created.ExtractionId, retrieved.ExtractionId);
        Assert.Single(retrieved.Medications);
        Assert.Equal("amoxicillin", retrieved.Medications[0].Name, ignoreCase: true);
        Assert.Equal(2, retrieved.Investigations.Count);

        // 2. GET /api/v1/extractions (History list)
        var getListResp = await _client.GetAsync("/api/v1/extractions?limit=10");
        Assert.Equal(HttpStatusCode.OK, getListResp.StatusCode);

        var historyList = await getListResp.Content.ReadFromJsonAsync<List<ClinicalExtractionResponse>>();
        Assert.NotNull(historyList);
        Assert.Contains(historyList, r => r.ExtractionId == created.ExtractionId);
    }

    [Fact]
    public async Task Extractions_GetById_Returns404_WhenNotFound()
    {
        string missingId = Guid.NewGuid().ToString("D");
        var response = await _client.GetAsync($"/api/v1/extractions/{missingId}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public void SqlExtractionRepository_SchemaDdl_DefinesRequiredTablesAndCascades()
    {
        string ddl = Apollo.Backend.Storage.SqlExtractionRepository.SchemaDdl;
        Assert.Contains("CREATE TABLE IF NOT EXISTS extraction_runs", ddl);
        Assert.Contains("CREATE TABLE IF NOT EXISTS extracted_medications", ddl);
        Assert.Contains("CREATE TABLE IF NOT EXISTS extracted_investigations", ddl);
        Assert.Contains("CREATE TABLE IF NOT EXISTS oov_quarantine_items", ddl);
        Assert.Contains("REFERENCES extraction_runs(extraction_id) ON DELETE CASCADE", ddl);
        Assert.Contains("CREATE EXTENSION IF NOT EXISTS vector;", ddl);
    }

    [Fact]
    public async Task FhirExport_TransformsStoredExtraction_IntoMedicationRequestAndDiagnosticReportBundle()
    {
        string note = "Plan: Start amoxicillin 125 mg PO BID for 7 days. Investigations: CBC and Chest X-ray.";
        var postResp = await _client.PostAsJsonAsync("/api/v1/extractions", new ClinicalExtractionRequest(note));
        Assert.Equal(HttpStatusCode.OK, postResp.StatusCode);

        var created = await postResp.Content.ReadFromJsonAsync<ClinicalExtractionResponse>();
        Assert.NotNull(created);

        var fhirResp = await _client.GetAsync($"/api/v1/extractions/{created.ExtractionId}/fhir?patientId=PAT-900");
        Assert.Equal(HttpStatusCode.OK, fhirResp.StatusCode);

        string json = await fhirResp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("Bundle", root.GetProperty("resourceType").GetString());
        Assert.Equal("collection", root.GetProperty("type").GetString());

        var entries = root.GetProperty("entry").EnumerateArray().ToList();
        Assert.Equal(3, entries.Count);

        var resourceTypes = entries.Select(e => e.GetProperty("resource").GetProperty("resourceType").GetString()).ToList();
        Assert.Contains("MedicationRequest", resourceTypes);
        Assert.Contains("DiagnosticReport", resourceTypes);
    }

    [Fact]
    public async Task FhirExport_TransformsClinicalNerResponse_IntoMedicationRequestAndDiagnosticReportBundle()
    {
        var nerResponse = new ClinicalNERResponse(
            Entities:
            [
                new ClinicalEntityResponse("MEDICATION", "Metformin", 0, 9, 0.98, "PRESENT", "Positive", "Current", "Patient", "MEDICATIONS", "RxNorm:860975", "Metformin"),
                new ClinicalEntityResponse("LAB", "CBC", 20, 23, 0.96, "PRESENT", "Positive", "Current", "Patient", "LABS", "LOINC:58410-2", "Complete Blood Count")
            ],
            StructuredMedications:
            [
                new StructuredMedicationResponse("Metformin", [0, 9], "500 mg", "500 mg", "BID", "PO", "for 30 days", "Positive", "Patient", "RxNorm:860975")
            ],
            MicroCard: "[PT] DX:NONE | RX:Metformin 500 mg | ALG:NKDA | LAB:CBC | VIT:NONE",
            FhirBundle: null,
            LatencyMs: 12.5
        );

        var response = await _client.PostAsJsonAsync("/api/v1/fhir/export?patientId=PAT-901", nerResponse);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var entries = doc.RootElement.GetProperty("entry").EnumerateArray().ToList();
        Assert.Equal(2, entries.Count);
        Assert.Equal("MedicationRequest", entries[0].GetProperty("resource").GetProperty("resourceType").GetString());
        Assert.Equal("DiagnosticReport", entries[1].GetProperty("resource").GetProperty("resourceType").GetString());
    }

    [Fact]
    public async Task Quarantine_EnqueuesScrubbedItem_AndAllowsClinicianResolution()
    {
        var createReq = new CreateQuarantineItemRequest(
            ExtractionId: null,
            Category: "MEDICATION",
            Mention: "Novalgin-X",
            Confidence: 0.62,
            Reason: "low_confidence",
            StartChar: 10,
            EndChar: 20,
            Context: "Patient phone +91 9876543210 took Novalgin-X yesterday."
        );

        var postResp = await _client.PostAsJsonAsync("/api/v1/quarantine", createReq);
        Assert.Equal(HttpStatusCode.OK, postResp.StatusCode);

        var created = await postResp.Content.ReadFromJsonAsync<QuarantineItemDto>();
        Assert.NotNull(created);
        Assert.Equal("pending", created.ReviewStatus);
        Assert.DoesNotContain("9876543210", created.ScrubbedContext);

        // List pending quarantine items
        var listResp = await _client.GetAsync("/api/v1/quarantine?status=pending");
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        var pendingList = await listResp.Content.ReadFromJsonAsync<List<QuarantineItemDto>>();
        Assert.NotNull(pendingList);
        Assert.Contains(pendingList, q => q.QuarantineId == created.QuarantineId);

        // Resolve quarantine item
        var resolveReq = new QuarantineResolutionRequest(
            ReviewStatus: "approved",
            MappedCode: "RX-NOV-500",
            MappedDisplay: "Metamizole 500mg",
            ReviewerNotes: "Mapped to hospital formulary"
        );

        var resolveResp = await _client.PostAsJsonAsync($"/api/v1/quarantine/{created.QuarantineId}/resolve", resolveReq);
        Assert.Equal(HttpStatusCode.OK, resolveResp.StatusCode);

        var resolved = await resolveResp.Content.ReadFromJsonAsync<QuarantineItemDto>();
        Assert.NotNull(resolved);
        Assert.Equal("approved", resolved.ReviewStatus);
        Assert.Equal("RX-NOV-500", resolved.MappedCode);
        Assert.NotNull(resolved.ReviewedAt);
    }

    [Fact]
    public async Task SemanticVectorSearch_GeneratesEmbeddings_AndRanksSimilarClinicalPresentationsFirst()
    {
        string diabetesNote = "58-year-old male with uncontrolled type 2 diabetes. Continue Metformin 1000 mg PO BID. Order HbA1c.";
        string pneumoniaNote = "62-year-old male with community acquired pneumonia and cough. Start Amoxicillin 500 mg PO TID. Order Chest X-ray.";

        var resp1 = await _client.PostAsJsonAsync("/api/v1/extractions", new ClinicalExtractionRequest(diabetesNote));
        var extDiabetes = await resp1.Content.ReadFromJsonAsync<ClinicalExtractionResponse>();
        Assert.NotNull(extDiabetes);
        Assert.NotNull(extDiabetes.Embedding);
        Assert.Equal(64, extDiabetes.Embedding.Length);

        var resp2 = await _client.PostAsJsonAsync("/api/v1/extractions", new ClinicalExtractionRequest(pneumoniaNote));
        var extPneumonia = await resp2.Content.ReadFromJsonAsync<ClinicalExtractionResponse>();
        Assert.NotNull(extPneumonia);

        // Search semantically for diabetes/glycemic management
        var searchResp = await _client.PostAsJsonAsync(
            "/api/v1/extractions/search",
            new SemanticSearchRequest("diabetic glycemic control metformin hba1c", Limit: 5, MinSimilarity: 0.1));
        Assert.Equal(HttpStatusCode.OK, searchResp.StatusCode);

        var searchResults = await searchResp.Content.ReadFromJsonAsync<List<SemanticSearchResultDto>>();
        Assert.NotNull(searchResults);
        Assert.NotEmpty(searchResults);
        Assert.Equal(extDiabetes.ExtractionId, searchResults[0].Extraction.ExtractionId);
        Assert.True(searchResults[0].SimilarityScore > 0.3);
    }

    [Fact]
    public async Task ClinicalSafetyEngine_FlagsDrugInteractions_Contraindications_AndCriticalLabAnomalies()
    {
        string highRiskNote = """
            Patient with atrial fibrillation and hypertension.
            Labs: Potassium 6.2 mEq/L, Serum Creatinine 2.4 mg/dL, hyperkalemia noted.
            Plan:
            1. Start Warfarin 5 mg PO daily.
            2. Start Aspirin 100 mg PO daily.
            3. Continue Lisinopril 10 mg PO daily.
            """;

        var resp = await _client.PostAsJsonAsync("/api/v1/extractions", new ClinicalExtractionRequest(highRiskNote));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var extraction = await resp.Content.ReadFromJsonAsync<ClinicalExtractionResponse>();
        Assert.NotNull(extraction);
        Assert.NotNull(extraction.SafetyAlerts);
        Assert.NotEmpty(extraction.SafetyAlerts);

        Assert.Contains(extraction.SafetyAlerts, a => a.Category == "DRUG_INTERACTION" && a.Severity == "HIGH");
        Assert.Contains(extraction.SafetyAlerts, a => a.Category == "CONTRAINDICATION" && a.Severity == "CRITICAL");
        Assert.Contains(extraction.SafetyAlerts, a => a.Category == "LAB_ANOMALY" && a.Severity == "CRITICAL");
    }

    [Fact]
    public async Task ActiveLearningFeedbackLoop_UpdatesRuntimeOntology_Dataset_AndPromptTemplates()
    {
        // 1. Enqueue an OOV medication into quarantine
        var createReq = new CreateQuarantineItemRequest(
            ExtractionId: null,
            Category: "MEDICATION",
            Mention: "CardioShieldXL",
            Confidence: 0.65,
            Reason: "unmapped_rxnorm",
            StartChar: 6,
            EndChar: 20,
            Context: "Start CardioShieldXL 50 mg PO OD."
        );

        var createResp = await _client.PostAsJsonAsync("/api/v1/quarantine", createReq);
        var qItem = await createResp.Content.ReadFromJsonAsync<QuarantineItemDto>();
        Assert.NotNull(qItem);

        // 2. Clinician approves and maps the quarantined item
        var resolveReq = new QuarantineResolutionRequest(
            ReviewStatus: "approved",
            MappedCode: "RX-CSX-50",
            MappedDisplay: "Bisoprolol XL 50mg",
            ReviewerNotes: "Approved for active learning"
        );

        var resolveResp = await _client.PostAsJsonAsync($"/api/v1/quarantine/{qItem.QuarantineId}/resolve", resolveReq);
        Assert.Equal(HttpStatusCode.OK, resolveResp.StatusCode);

        // 3. Verify active learning state reflects the newly learned term and prompt exemplar
        var stateResp = await _client.GetAsync("/api/v1/active-learning/state");
        Assert.Equal(HttpStatusCode.OK, stateResp.StatusCode);
        var state = await stateResp.Content.ReadFromJsonAsync<ActiveLearningStateDto>();
        Assert.NotNull(state);
        Assert.True(state.LearnedMedicationCount >= 1);
        Assert.Contains(state.PromptExemplars, p => p.Contains("CardioShieldXL"));

        // 4. Verify subsequent extraction immediately recognizes and normalizes "CardioShieldXL"
        var extResp = await _client.PostAsJsonAsync(
            "/api/v1/extractions",
            new ClinicalExtractionRequest("Plan: Start CardioShieldXL 50 mg PO OD for 30 days."));
        Assert.Equal(HttpStatusCode.OK, extResp.StatusCode);

        var extResult = await extResp.Content.ReadFromJsonAsync<ClinicalExtractionResponse>();
        Assert.NotNull(extResult);
        var learnedMed = Assert.Single(extResult.Medications, m => m.Name.Equals("CardioShieldXL", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("uncertain", learnedMed.Normalization.Status);
        Assert.Null(learnedMed.Normalization.MatchedCode); // A local catalog label is not a verified RxCUI.
        Assert.Equal("Bisoprolol XL 50mg", learnedMed.Normalization.DisplayName);
    }
}

