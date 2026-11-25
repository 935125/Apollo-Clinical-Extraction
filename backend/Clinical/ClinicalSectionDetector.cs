using System.Text.RegularExpressions;

namespace Apollo.Backend.Clinical;

/// <summary>Line-preserving segmentation; all indices refer to the unchanged .NET UTF-16 source.</summary>
public static class ClinicalSectionDetector
{
    private static readonly (string Pattern, string Code)[] Headings =
    [
        (@"allergy history|allergies|documented allergies|drug allergies|allergy", "ALLERGIES"),
        (@"discontinued medications?|stopped medications?", "DISCONTINUED_MEDICATIONS"),
        (@"drug history|medication history|past meds?|past medications?", "MEDICATION_HISTORY"),
        (@"discharge medications?|current medications?|current therapy|medication orders?|medications?|meds|prescriptions?|rx", "MEDICATIONS"),
        (@"chemotherapy regimen|chemotherapy administered|chemotherapy", "CHEMOTHERAPY"),
        (@"inpatient treatment|current treatment|treatment given", "CURRENT_TREATMENT"),
        (@"hospital course(?:\s*&\s*(?:clinical presentation|plan))?|clinical presentation", "HOSPITAL_COURSE"),
        (@"past medical history|pmh|past history|medical history|surgical history", "PAST_MEDICAL_HISTORY"),
        (@"family history|family medical history|fhx", "FAMILY_HISTORY"),
        (@"prior investigations?|historical investigations?", "INVESTIGATION_HISTORY"),
        (@"investigations? (?:performed|done)|laboratory results?|diagnostic results?|test results?|baseline labs?", "INVESTIGATIONS_PERFORMED"),
        (@"(?:outpatient )?follow[- ]up investigations?|investigations? planned|planned investigations?", "INVESTIGATIONS_PLANNED"),
        (@"follow[- ]up (?:instructions?|plan|advice)|follow[- ]up", "FOLLOW_UP_PLAN"),
        (@"investigations?|labs?|laboratory|imaging|diagnostic workup|workup", "LABS"),
        (@"assessment and plan|assessment & plan|treatment plan|differential treatment options|plan|recommendations?|advice|assessment", "ASSESSMENT_PLAN"),
        (@"patient education|education|counselling|counseling", "EDUCATION"),
        (@"primary diagnosis|discharge diagnoses|diagnoses|diagnosis|impression", "DIAGNOSES"),
        (@"discharge vital signs(?:\s*&\s*baseline labs?)?|physical examination|examination|exam|o/e|vital signs?|vitals?", "EXAMINATION"),
        (@"history of present illness|chief complaint|presenting complaints?|reasons? for admission|subjective", "CHIEF_COMPLAINT")
    ];
    private static readonly (Regex Pattern, string Code)[] Patterns = Headings.Select(h =>
        (new Regex(@"^[\t #*\u2022-]*(?:\d+[.)][\t ]*)?(?<heading>"+h.Pattern+@")(?=[\t ]*(?::|$))[\t ]*:?[\t ]*",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(1)), h.Code)).ToArray();

    public static List<ClinicalSection> Detect(string source)
    {
        var starts = new List<(int Start, string Heading, string Code)>();
        foreach (Match line in Regex.Matches(source, @"(?m)^[^\r\n]+"))
        {
            foreach (var (pattern, code) in Patterns)
            {
                var match = pattern.Match(line.Value);
                if (!match.Success) continue;
                starts.Add((line.Index, match.Groups["heading"].Value, code));
                break;
            }
        }
        if (starts.Count == 0) return [new("OTHER", "Body", 0, source.Length)];
        var sections = new List<ClinicalSection>();
        if (starts[0].Start > 0) sections.Add(new("OTHER", "Prologue", 0, starts[0].Start));
        for (int i=0; i<starts.Count; i++)
            sections.Add(new(starts[i].Code, starts[i].Heading, starts[i].Start,
                i+1<starts.Count ? starts[i+1].Start : source.Length));
        return sections;
    }
}
