using System.Collections.Concurrent;
using System.Text.Json;
using Apollo.Backend.Models;
using Apollo.Backend.Security;

namespace Apollo.Backend.Clinical;

public class ActiveLearningFeedbackService
{
    private static readonly Lock IoLock = new();
    private readonly ConcurrentQueue<ActiveLearningFeedbackDto> _feedbackHistory = new();
    private readonly ConcurrentBag<string> _promptExemplars = [];
    private int _datasetEntriesCount;

    public ActiveLearningFeedbackDto ApplyResolution(
        QuarantineItemDto item,
        QuarantineResolutionRequest resolution,
        string? datasetLogPath = null,
        string? promptTemplatesPath = null)
    {
        string mappedCode = string.IsNullOrWhiteSpace(resolution.MappedCode) ? "UNSPECIFIED" : resolution.MappedCode.Trim();
        string mappedDisplay = string.IsNullOrWhiteSpace(resolution.MappedDisplay) ? item.Mention : resolution.MappedDisplay.Trim();
        string scrubbedCtx = ZeroLeakageValidator.Scrub(item.ScrubbedContext);

        // 1. Dynamically update live ontology dictionary for immediate runtime recall & normalization
        if (item.Category.Equals("MEDICATION", StringComparison.OrdinalIgnoreCase))
        {
            string rxCui = mappedCode.Replace("RxNorm:", "", StringComparison.OrdinalIgnoreCase);
            IndianOntologyResolver.RegisterLearnedMedication(item.Mention, mappedDisplay, mappedCode, rxCui);
        }
        else if (item.Category.Equals("INVESTIGATION", StringComparison.OrdinalIgnoreCase) ||
                 item.Category.Equals("LAB", StringComparison.OrdinalIgnoreCase) ||
                 item.Category.Equals("LAB_TEST", StringComparison.OrdinalIgnoreCase))
        {
            string loinc = mappedCode.Replace("LOINC:", "", StringComparison.OrdinalIgnoreCase);
            IndianOntologyResolver.RegisterLearnedInvestigation(item.Mention, mappedDisplay, mappedCode, loinc);
        }

        // 2. Build prompt-tuning exemplar
        string exemplar = $"[MAPPING_REQUIRES_VERIFICATION] Mention: \"{item.Mention}\" -> Category: {item.Category}, Code: {mappedCode} ({mappedDisplay}) | Context: \"{scrubbedCtx}\"";
        _promptExemplars.Add(exemplar);

        var feedback = new ActiveLearningFeedbackDto(
            FeedbackId: Guid.NewGuid().ToString("D"),
            QuarantineId: item.QuarantineId,
            Category: item.Category,
            Mention: item.Mention,
            NormalizedMention: item.NormalizedMention,
            MappedCode: mappedCode,
            MappedDisplay: mappedDisplay,
            ScrubbedContext: scrubbedCtx,
            PromptExemplar: exemplar,
            CreatedAt: DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:sszzz")
        );

        _feedbackHistory.Enqueue(feedback);
        Interlocked.Increment(ref _datasetEntriesCount);

        // Source-bearing training artifacts are opt-in via explicit paths, never routine API logs.
        if (datasetLogPath == null || promptTemplatesPath == null) return feedback;
        // 3. Explicit offline export only; callers own authorization, redaction and retention.
        try
        {
            string projectDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", ".."));
            string backendDir = Path.GetFileName(projectDir).Equals("tests", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFullPath(Path.Combine(projectDir, ".."))
                : projectDir;
            string baseLogsDir = Path.Combine(backendDir, "logs");
            string fullDatasetPath = Path.GetFullPath(datasetLogPath ?? Path.Combine(baseLogsDir, "active_learning_dataset.jsonl"));
            string fullTemplatesPath = Path.GetFullPath(promptTemplatesPath ?? Path.Combine(baseLogsDir, "prompt_tuning_templates.json"));

            var dir = Path.GetDirectoryName(fullDatasetPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string jsonLine = JsonSerializer.Serialize(new
            {
                ts = feedback.CreatedAt,
                quarantine_id = feedback.QuarantineId,
                category = feedback.Category,
                mention = feedback.Mention,
                normalized = feedback.NormalizedMention,
                mapped_code = feedback.MappedCode,
                mapped_display = feedback.MappedDisplay,
                context = feedback.ScrubbedContext,
                prompt_exemplar = feedback.PromptExemplar
            });

            lock (IoLock)
            {
                File.AppendAllText(fullDatasetPath, jsonLine + Environment.NewLine, System.Text.Encoding.UTF8);

                var templatePayload = new
                {
                    schemaVersion = "1.0.0",
                    updatedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:sszzz"),
                    learnedMedications = IndianOntologyResolver.LearnedDrugCount,
                    learnedInvestigations = IndianOntologyResolver.LearnedLabCount,
                    fewShotExemplars = _promptExemplars.TakeLast(50).ToList()
                };

                File.WriteAllText(
                    fullTemplatesPath,
                    JsonSerializer.Serialize(templatePayload, new JsonSerializerOptions { WriteIndented = true }),
                    System.Text.Encoding.UTF8);
            }
        }
        catch
        {
            // Never crash request pipeline on disk I/O exceptions
        }

        return feedback;
    }

    public ActiveLearningStateDto GetState()
    {
        return new ActiveLearningStateDto(
            LearnedMedicationCount: IndianOntologyResolver.LearnedDrugCount,
            LearnedInvestigationCount: IndianOntologyResolver.LearnedLabCount,
            DatasetEntriesCount: _datasetEntriesCount,
            PromptExemplars: _promptExemplars.TakeLast(25).ToList(),
            RecentFeedback: _feedbackHistory.Reverse().Take(25).ToList()
        );
    }
}

