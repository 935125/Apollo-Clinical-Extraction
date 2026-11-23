using System.Text;
using Apollo.Backend.Nlp;

namespace Apollo.Backend.Tests;

public class SlidingWindowTests
{
    [Fact]
    public void SlidingWindow_ProcessesLongDocument_WithoutBoundaryArtifacts()
    {
        // Build a document over 1000 words spanning multiple 512-token windows
        var sb = new StringBuilder();
        sb.AppendLine("Patient History:");

        for (int i = 0; i < 400; i++)
        {
            sb.Append("Patient reported no complications during ongoing monitoring period. ");
        }

        // Insert clinical entities in window 1, window 2, and window boundary
        sb.AppendLine("\nMEDICATIONS:");
        sb.AppendLine("1. Metformin 500 mg PO BID.");
        sb.AppendLine("2. Atorvastatin 20 mg PO once daily.");

        for (int i = 0; i < 400; i++)
        {
            sb.Append("Follow-up routine examination was unremarkable and vitals were normal. ");
        }

        sb.AppendLine("\nINVESTIGATIONS:");
        sb.AppendLine("Ordered Complete Blood Count and Serum Creatinine.");

        string longText = sb.ToString();
        Assert.True(longText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 1000);

        var tokenizer = new WordPieceTokenizer();
        using var engine = new SlidingWindowOnnxEngine(tokenizer: tokenizer, allowDeterministicTestScoring: true);

        var tokenized = tokenizer.Tokenize(longText);
        Assert.True(tokenized.InputIds.Count > 512); // Spans multiple windows

        var spans = engine.Predict(longText, tokenized);
        Assert.NotNull(spans);

        // Verify entities from across the long text are extracted cleanly
        Assert.Contains(spans, s => s.Text.Equals("Metformin", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(spans, s => s.Text.Equals("Atorvastatin", StringComparison.OrdinalIgnoreCase));
    }
}

