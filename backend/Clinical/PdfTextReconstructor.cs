using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace Apollo.Backend.Clinical;

public sealed record PdfTableCell(int Row, int Column, double X, double Y, string Text);

public sealed record PdfLineCoordinateSpan(
    int PageNumber,
    int LineIndex,
    int StartChar,
    int EndChar,
    double Left,
    double Right,
    double Bottom,
    double Top,
    string Text
);

public sealed record PdfReconstructedPage(
    int PageNumber,
    string RawPageText,
    string ReconstructedText,
    IReadOnlyList<string> Lines,
    IReadOnlyList<PdfLineCoordinateSpan>? CoordinateSpans = null
);

public sealed record PdfReconstructionResult(
    string RawConcatenatedText,
    string ReconstructedText,
    IReadOnlyList<PdfReconstructedPage> Pages,
    IReadOnlyList<PdfLineCoordinateSpan>? CoordinateMap = null
);

/// <summary>
/// Coordinate-aware PdfPig text and table reconstructor that preserves word boundaries,
/// line breaks, section headings, medication list boundaries, and 2D table header/value associations
/// without invoking OCR on selectable-text PDFs.
/// </summary>
public static partial class PdfTextReconstructor
{
    public static string ExtractText(Stream pdfStream) => Reconstruct(pdfStream).ReconstructedText;

    public static string ExtractText(byte[] pdfBytes)
    {
        using var ms = new MemoryStream(pdfBytes, writable: false);
        return Reconstruct(ms).ReconstructedText;
    }

    public static PdfReconstructionResult Reconstruct(Stream pdfStream)
    {
        using var document = PdfDocument.Open(pdfStream);
        return Reconstruct(document);
    }

    public static PdfReconstructionResult Reconstruct(PdfDocument document)
    {
        var rawPages = new List<string>();
        var reconstructedPages = new List<PdfReconstructedPage>();
        var globalSpans = new List<PdfLineCoordinateSpan>();
        int globalCharOffset = 0;

        foreach (var page in document.GetPages())
        {
            string rawPageText = page.Text ?? string.Empty;
            rawPages.Add(rawPageText);

            var words = page.GetWords()
                .Where(w => !string.IsNullOrWhiteSpace(w.Text))
                .ToList();

            if (words.Count == 0)
            {
                string fallback = NormalizeConcatenatedBoundaries(rawPageText);
                var fallbackLines = string.IsNullOrWhiteSpace(fallback) ? Array.Empty<string>() : fallback.Split('\n');
                reconstructedPages.Add(new PdfReconstructedPage(page.Number, rawPageText, fallback, fallbackLines, []));
                continue;
            }

            var (lines, lineBoxes) = ReconstructPageLinesWithCoordinates(words);
            string pageText = string.Join("\n", lines);
            if (!string.IsNullOrWhiteSpace(pageText) && globalSpans.Count > 0)
            {
                globalCharOffset += 2; // Account for "\n\n" between pages
            }

            var pageSpans = new List<PdfLineCoordinateSpan>(lines.Count);
            int localOffset = globalCharOffset;
            for (int idx = 0; idx < lines.Count; idx++)
            {
                string lineText = lines[idx];
                var box = lineBoxes[idx];
                int start = localOffset;
                int end = start + lineText.Length;
                var span = new PdfLineCoordinateSpan(page.Number, idx, start, end, box.Left, box.Right, box.Bottom, box.Top, lineText);
                pageSpans.Add(span);
                globalSpans.Add(span);
                localOffset = end + 1; // +1 for '\n'
            }
            if (!string.IsNullOrWhiteSpace(pageText))
            {
                globalCharOffset += pageText.Length;
            }

            reconstructedPages.Add(new PdfReconstructedPage(page.Number, rawPageText, pageText, lines, pageSpans));
        }

        string rawConcat = string.Join("\n\n", rawPages);
        string fullReconstructed = string.Join("\n\n", reconstructedPages.Select(p => p.ReconstructedText).Where(t => !string.IsNullOrWhiteSpace(t)));
        return new PdfReconstructionResult(rawConcat, fullReconstructed, reconstructedPages, globalSpans);
    }

    private sealed record WordItem(string Text, double Left, double Right, double Bottom, double Top, double Height);

    private sealed class VisualLine
    {
        public double ReferenceBottom { get; set; }
        public double MaxHeight { get; set; }
        public double MinBottom { get; set; }
        public double MaxTop { get; set; }
        public List<WordItem> Words { get; } = [];
    }

    private sealed record LineBox(double Left, double Right, double Bottom, double Top);

    private static (List<string> Lines, List<LineBox> Boxes) ReconstructPageLinesWithCoordinates(IReadOnlyList<Word> words)
    {
        var items = words.Select(w => new WordItem(
                w.Text.Trim(),
                w.BoundingBox.Left,
                w.BoundingBox.Right,
                w.BoundingBox.Bottom,
                w.BoundingBox.Top,
                Math.Max(1.0, w.BoundingBox.Height)))
            .Where(w => w.Text.Length > 0)
            .OrderByDescending(w => w.Bottom)
            .ThenBy(w => w.Left)
            .ToList();

        var visualLines = new List<VisualLine>();
        foreach (var word in items)
        {
            VisualLine? target = null;
            double bestDelta = double.MaxValue;
            foreach (var line in visualLines)
            {
                double tolerance = Math.Max(3.5, Math.Min(line.MaxHeight, word.Height) * 0.45);
                double delta = Math.Abs(line.ReferenceBottom - word.Bottom);
                if (delta <= tolerance && delta < bestDelta)
                {
                    bestDelta = delta;
                    target = line;
                }
            }

            if (target == null)
            {
                target = new VisualLine
                {
                    ReferenceBottom = word.Bottom,
                    MaxHeight = word.Height,
                    MinBottom = word.Bottom,
                    MaxTop = word.Top
                };
                target.Words.Add(word);
                visualLines.Add(target);
            }
            else
            {
                target.Words.Add(word);
                target.ReferenceBottom = target.Words.Average(w => w.Bottom);
                target.MaxHeight = Math.Max(target.MaxHeight, word.Height);
                target.MinBottom = Math.Min(target.MinBottom, word.Bottom);
                target.MaxTop = Math.Max(target.MaxTop, word.Top);
            }
        }

        visualLines.Sort((a, b) => b.ReferenceBottom.CompareTo(a.ReferenceBottom));

        // Detect multi-column table rows (e.g., Vital Signs & Baseline Labs header row + value row)
        var rawLineClusters = visualLines.Select(ClusterLineIntoColumns).ToList();
        var outputLines = new List<string>();
        var outputBoxes = new List<LineBox>();

        LineBox BoxForVisualLine(VisualLine vl) => new(
            vl.Words.Min(w => w.Left),
            vl.Words.Max(w => w.Right),
            vl.MinBottom,
            vl.MaxTop);

        for (int i = 0; i < visualLines.Count; i++)
        {
            if (i > 0)
            {
                double verticalGap = visualLines[i - 1].MinBottom - visualLines[i].MaxTop;
                double avgHeight = (visualLines[i - 1].MaxHeight + visualLines[i].MaxHeight) * 0.5;
                if (verticalGap > Math.Max(8.0, avgHeight * 0.85) && outputLines.Count > 0 && outputLines[^1].Length > 0)
                {
                    outputLines.Add(string.Empty);
                    outputBoxes.Add(new LineBox(0, 0, visualLines[i].MaxTop, visualLines[i - 1].MinBottom));
                }
            }

            var currentCols = rawLineClusters[i];
            if (currentCols.Count >= 3 && i + 1 < visualLines.Count)
            {
                var nextCols = rawLineClusters[i + 1];
                if (nextCols.Count == currentCols.Count &&
                    IsHeaderValueTablePair(currentCols, nextCols))
                {
                    var pairs = new List<string>(currentCols.Count);
                    for (int c = 0; c < currentCols.Count; c++)
                    {
                        pairs.Add($"{currentCols[c].Text}: {nextCols[c].Text}");
                    }
                    outputLines.Add(string.Join(" | ", pairs));
                    var b1 = BoxForVisualLine(visualLines[i]);
                    var b2 = BoxForVisualLine(visualLines[i + 1]);
                    outputBoxes.Add(new LineBox(Math.Min(b1.Left, b2.Left), Math.Max(b1.Right, b2.Right), Math.Min(b1.Bottom, b2.Bottom), Math.Max(b1.Top, b2.Top)));
                    i++; // Consumed the value row paired with the header row
                    continue;
                }
            }

            string lineText = FormatLineFromColumns(currentCols);
            if (!IsPageFooterNoise(lineText, i, visualLines.Count))
            {
                var splitLines = lineText.Split('\n');
                var box = BoxForVisualLine(visualLines[i]);
                foreach (var sl in splitLines)
                {
                    outputLines.Add(sl);
                    outputBoxes.Add(box);
                }
            }
        }

        return (outputLines, outputBoxes);
    }

    private sealed record ColumnCluster(double Left, double Right, string Text);

    private static List<ColumnCluster> ClusterLineIntoColumns(VisualLine line)
    {
        var ordered = line.Words.OrderBy(w => w.Left).ToList();
        if (ordered.Count == 0) return [];

        var clusters = new List<ColumnCluster>();
        double clusterLeft = ordered[0].Left;
        double clusterRight = ordered[0].Right;
        var sb = new StringBuilder(ordered[0].Text);

        for (int i = 1; i < ordered.Count; i++)
        {
            var prev = ordered[i - 1];
            var curr = ordered[i];
            double gap = curr.Left - prev.Right;
            double avgCharWidth = Math.Max(2.5, prev.Height * 0.45);

            // A large horizontal gap (>= 22pt or >= 2.4x char height) separates table columns
            if (gap >= Math.Max(22.0, prev.Height * 2.4))
            {
                clusters.Add(new ColumnCluster(clusterLeft, clusterRight, NormalizeConcatenatedBoundaries(sb.ToString())));
                sb.Clear();
                sb.Append(curr.Text);
                clusterLeft = curr.Left;
                clusterRight = curr.Right;
            }
            else
            {
                bool needsSpace = gap > -avgCharWidth * 0.25 &&
                                  !EndsWithOpenPunctuation(sb) &&
                                  !StartsWithClosePunctuation(curr.Text) &&
                                  !IsSplitWordContinuation(prev, curr, gap, avgCharWidth);
                if (needsSpace) sb.Append(' ');
                sb.Append(curr.Text);
                clusterRight = Math.Max(clusterRight, curr.Right);
            }
        }

        clusters.Add(new ColumnCluster(clusterLeft, clusterRight, NormalizeConcatenatedBoundaries(sb.ToString())));

        // If this visual line is a prose line rather than a multi-column table row (e.g. a single bullet/sentence
        // where PdfPig detected a wide kerning gap inside one sentence), collapse into a single column unless
        // every column looks like a short table cell (<= 38 chars).
        if (clusters.Count > 1 && clusters.Any(c => c.Text.Length > 38))
        {
            return [new ColumnCluster(clusters[0].Left, clusters[^1].Right, string.Join(" ", clusters.Select(c => c.Text)))];
        }

        return clusters;
    }

    private static bool IsSplitWordContinuation(WordItem prev, WordItem curr, double gap, double avgCharWidth)
    {
        // Join chemical ion sign attached to single element symbol (e.g. "K" + "+" -> "K+", "Na" + "+" -> "Na+")
        if (curr.Text is "+" or "-" && prev.Text is "K" or "Na" or "Ca" or "Mg" or "Cl" && gap <= avgCharWidth * 0.8)
            return true;

        // PdfPig sometimes splits a single word across two Word tokens when kerning is slightly adjusted
        // (e.g. "P" + "rothrombin" or "Proth" + "rombin" or "Fol" + "low-up"). When the gap is tiny
        // (<= 0.45 * avgCharWidth) and prev ends with a letter while curr starts with a lowercase letter, keep joined.
        if (gap <= avgCharWidth * 0.45 &&
            prev.Text.Length >= 1 && char.IsLetter(prev.Text[^1]) &&
            curr.Text.Length >= 2 && char.IsLower(curr.Text[0]))
        {
            return true;
        }
        return false;
    }

    private static bool IsHeaderValueTablePair(List<ColumnCluster> headers, List<ColumnCluster> values)
    {
        // Headers are predominantly alphabetic labels ("Blood Pressure", "Heart Rate", "Temperature", "Serum Potassium & Creatinine")
        // while values contain clinical measurements/digits ("124/76 mmHg", "82 bpm", "36.8 °C", "K+ 5.7 mEq/L, Creatinine 1.9 mg/dL")
        bool headersLookLikeLabels = headers.All(h => !Regex.IsMatch(h.Text, @"^\d")) &&
                                     headers.Count(h => Regex.IsMatch(h.Text, @"\d")) <= 1;
        bool valuesHaveNumbers = values.Count(v => Regex.IsMatch(v.Text, @"\d")) >= Math.Max(2, values.Count - 1);
        if (!headersLookLikeLabels || !valuesHaveNumbers)
            return false;

        int aligned = 0;
        for (int c = 0; c < headers.Count; c++)
        {
            double hCenter = (headers[c].Left + headers[c].Right) * 0.5;
            double vCenter = (values[c].Left + values[c].Right) * 0.5;
            bool overlaps = Math.Abs(headers[c].Left - values[c].Left) <= 35.0 ||
                            Math.Abs(hCenter - vCenter) <= 55.0;
            if (overlaps) aligned++;
        }
        return aligned >= headers.Count - 1;
    }

    private static string FormatLineFromColumns(List<ColumnCluster> cols)
    {
        if (cols.Count == 0) return string.Empty;
        if (cols.Count == 1) return cols[0].Text;
        return string.Join(" | ", cols.Select(c => c.Text));
    }

    private static bool IsPageFooterNoise(string lineText, int lineIndex, int totalLines)
    {
        if (lineIndex < totalLines - 2) return false;
        return Regex.IsMatch(lineText.Trim(), @"^Page\s+\d+\s+of\s+\d+$", RegexOptions.IgnoreCase);
    }

    private static bool EndsWithOpenPunctuation(StringBuilder sb) =>
        sb.Length > 0 && sb[^1] is '(' or '[' or '{' or '/' or '-';

    private static bool StartsWithClosePunctuation(string text) =>
        text.Length > 0 && text[0] is '.' or ',' or ';' or ':' or ')' or ']' or '}';

    /// <summary>
    /// Repairs concatenated heading/word boundaries produced when PDF generators omit space glyphs
    /// at line or style transitions (e.g., "SUMMARYCardiology", "Presentation64-year-old", "MedicationsWarfarin").
    /// </summary>
    public static string NormalizeConcatenatedBoundaries(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        string s = input;
        // Uppercase acronym/word run followed by Capitalized word (e.g., "SUMMARYCardiology" -> "SUMMARY\nCardiology")
        s = Regex.Replace(s, @"([A-Z]{3,})([A-Z][a-z]{2,})", "$1\n$2");
        // Lowercase letter (after at least 2 letters, excluding CamelCase prefixes like Mc/Mac/De/Quanti) immediately followed by Uppercase word
        s = Regex.Replace(s, @"(?<![\bMm]c|[\bMm]ac|Quanti)([a-z]{2,})([A-Z][a-z]{2,})", "$1\n$2");
        // Sentence-ending period immediately followed by Capitalized word or numbered list item
        s = Regex.Replace(s, @"\.([A-Z][a-z]|\d+\.\s)", ".\n$1");
        // Letter immediately followed by age/numbered item or table cell measurement (e.g., "Presentation64-year-old", "Diagnoses1.", "Creatinine124/76")
        s = Regex.Replace(s, @"([A-Za-z])(\d+-year-old|\d+\.\s|\d{2,3}/\d{2,3}\s*mmHg)", "$1\n$2");
        return s.Trim();
    }
}
