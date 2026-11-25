using System.Text.RegularExpressions;
using Apollo.Backend.Models;

namespace Apollo.Backend.Clinical;

public sealed record LocalMedicationAttributes(string? Strength,string? Dose,string? DoseUnit,string? Route,string? Frequency,
    string? Duration,string? Formulation,string? Administration,string? StartTiming,string? StopTiming,string? PrnIndication,
    List<AttributeEvidenceDto> Evidence);

public static class MedicationAttributeExtractor
{
    private static readonly Regex Mass = new(@"\b\d+(?:\.\d+)?(?:\s*/\s*\d+(?:\.\d+)?)?\s*(?<unit>mg|mcg|µg|ug|gm|g|mEq)(?<denominator>\s*/\s*(?:\d+(?:\.\d+)?\s*)?(?:m2|m²|kg|mL|L))?(?![\p{L}\p{N}])|\b\d+(?:\.\d+)?\s*%",RegexOptions.IgnoreCase | RegexOptions.Compiled,TimeSpan.FromSeconds(1));
    private static readonly Regex Quantity = new(@"\b(?:\d+(?:\.\d+)?|1/2|half|one|two|three|four)\s*(?:mL|units?|IU|tablets?|tabs?|capsules?|caps?|puffs?|drops?|vials?|ampoules?|patch(?:es)?|sprays?|sachets?)\b",RegexOptions.IgnoreCase | RegexOptions.Compiled,TimeSpan.FromSeconds(1));
    private static readonly (string Pattern,string Value)[] Routes =
    [
        (@"\b(?:PO|oral|orally|by mouth|per os)\b","PO"),(@"\b(?:IV|intravenous|intravenously)\b","IV"),
        (@"\b(?:IM|intramuscular|intramuscularly)\b","IM"),(@"\b(?:SC|SQ|subcutaneous|subcutaneously|sub-q)\b","SC"),
        (@"\b(?:inhaled|inhalation|nebulized|via inhaler)\b","Inhalation"),(@"\b(?:ophthalmic|eye drops|each eye|both eyes)\b","Ophthalmic"),
        (@"\b(?:topical|topically|apply)\b","Topical"),(@"\b(?:intranasal|nasal spray)\b","Intranasal"),
        (@"\b(?:sublingual|under the tongue)\b","SL"),(@"\b(?:rectal|per rectum)\b","Rectal"),(@"\bintrathecal\b","Intrathecal")
    ];
    private static readonly (string Pattern,string Value)[] Frequencies =
    [
        (@"\b(?:BID|BD|twice daily|twice a day|1-0-1)\b","BID"),(@"\b(?:TID|TDS|three times daily|thrice daily|1-1-1)\b","TID"),
        (@"\b(?:QID|QDS|four times daily)\b","QID"),(@"\b(?:once weekly|weekly|every week)\b","WEEKLY"),
        (@"\b(?:QHS|HS|at bedtime|nightly|0-0-1)\b","QHS"),(@"\b(?:QD|OD|once daily|once a day|daily|every morning|1-0-0)\b","QD"),
        (@"\b(?:PRN|SOS|as needed|when required)\b","PRN"),(@"\b(?:STAT|immediately)\b","STAT")
    ];

    public static LocalMedicationAttributes Extract(string source,LocalEntitySpan entity,IReadOnlyList<LocalEntitySpan> all)
    {
        var range=LocalClinicalScope.Attributes(source,entity,all);
        string scope=source[range.Start..range.End];
        var evidence=new List<AttributeEvidenceDto>();
        string? Add(string field,string? value,Match? match)
        {
            if(value==null || match==null || !match.Success) return null;
            evidence.Add(new(field,value,new(source[(range.Start+match.Index)..(range.Start+match.Index+match.Length)],true,
                range.Start+match.Index,range.Start+match.Index+match.Length)));
            return value;
        }
        int relativeEnd=entity.End-range.Start;
        var masses=Mass.Matches(scope).Where(m=>m.Index>=relativeEnd || m.Index+m.Length<=entity.Start-range.Start).ToList();
        var quantities=Quantity.Matches(scope).Where(m=>!LocalClinicalScope.Has(scope[(m.Index+m.Length)..],@"^\s*/\s*(?:mL|L)\b")).ToList();
        var quantity=quantities.FirstOrDefault();
        var mass=masses.OrderBy(m=>m.Index>=relativeEnd ? m.Index-relativeEnd : entity.Start-range.Start-m.Index-m.Length).FirstOrDefault();
        bool concentration=mass!=null && LocalClinicalScope.Has(mass.Value,@"/\s*(?:\d+(?:\.\d+)?\s*)?(?:mL|L)\b|%");
        bool based=mass!=null && LocalClinicalScope.Has(mass.Value,@"/\s*(?:kg|m2|m²)\b");
        // A mass alone is an administered dose. Product strength is claimed only with a
        // documented form quantity, concentration, percentage or explicit strength label.
        bool product=mass!=null && !based && (quantity!=null || concentration || LocalClinicalScope.Has(scope,@"\bstrength\b"));
        string? strength=product ? Add("strength",mass!.Value.Trim(),mass) : null;
        string? dose=quantity!=null ? Add("dose",quantity.Value.Trim(),quantity) :
            mass!=null && !concentration ? Add("dose",mass.Value.Trim(),mass) : null;
        string? unit=dose==null ? null : Regex.Match(dose,@"(?:mg|mcg|µg|ug|gm|g|mEq)(?:\s*/\s*(?:m2|m²|kg))?|mL|units?|IU|tablets?|tabs?|capsules?|caps?|puffs?|drops?|vials?|ampoules?|patch(?:es)?|sprays?|sachets?",RegexOptions.IgnoreCase).Value;
        var routeMatches=Routes.Select(r=>(Match:Regex.Match(scope,r.Pattern,RegexOptions.IgnoreCase),r.Value)).Where(r=>r.Match.Success).ToList();
        string? route=routeMatches.Select(r=>r.Value).Distinct().Count()==1 ? Add("route",routeMatches[0].Value,routeMatches[0].Match) : null;
        var interval=Regex.Match(scope,@"\b(?:every\s+(?<hours>\d+)\s+hours|q\s*(?<hours>\d+)\s*h)\b(?:\s+(?:as needed|PRN|SOS))?",RegexOptions.IgnoreCase);
        string? frequency=null;
        if(interval.Success)
            frequency=Add("frequency",LocalClinicalScope.Has(interval.Value,@"\b(?:PRN|SOS|as needed)\b") ? interval.Value : "Q"+interval.Groups["hours"].Value+"H",interval);
        else
        {
            var matches=Frequencies.Select(f=>(Match:Regex.Match(scope,f.Pattern,RegexOptions.IgnoreCase),f.Value)).Where(f=>f.Match.Success).ToList();
            // "twice daily" also contains "daily"; longest expressions take precedence.
            var selected=matches.OrderByDescending(f=>f.Match.Length).FirstOrDefault();
            if(selected.Match!=null && !(selected.Match.Value.Equals("OD",StringComparison.OrdinalIgnoreCase) && route=="Ophthalmic"))
                frequency=Add("frequency",selected.Value,selected.Match);
        }
        var duration=Regex.Match(scope,@"\b(?:for\s+|x\s*)(?<value>\d+\s*(?:more\s+)?(?:days?|weeks?|months?|hours?))\b",RegexOptions.IgnoreCase);
        string? durationValue=Add("duration",duration.Success ? duration.Groups["value"].Value : null,duration);
        var form=Regex.Match(scope,@"\b(?:(?:oral|ophthalmic)\s+)?(?:tablets?|capsules?|syrup|suspension|solution|inhaler|injection|drops?|cream|ointment|gel)\b",RegexOptions.IgnoreCase);
        string? formulation=Add("formulation",form.Success ? form.Value : null,form);
        var administration=Regex.Match(scope,@"\bcontinuous\s+(?:IV\s+)?infusion\b|\bIV\s+push\b|\bIV\s+infusion\b",RegexOptions.IgnoreCase);
        string? administrationValue=Add("administration",administration.Success ?
            LocalClinicalScope.Has(administration.Value,@"continuous") ? "continuous infusion" : administration.Value : null,administration);
        var start=Regex.Match(scope,@"\b(?:from|starting|start on)\s+(?<value>\d{1,2}/\d{1,2}/\d{4})\b",RegexOptions.IgnoreCase);
        var stop=Regex.Match(scope,@"\b(?:until|stop on|through)\s+(?<value>\d{1,2}/\d{1,2}/\d{4})\b",RegexOptions.IgnoreCase);
        var prn=Regex.Match(scope,@"\b(?:PRN|SOS|as needed|when required)\s+for\s+(?<value>[A-Za-z]+(?:\s+(?!from\b|until\b|for\b)[A-Za-z]+){0,3})",RegexOptions.IgnoreCase);
        return new(strength,dose,unit,route,frequency,durationValue,formulation,administrationValue,
            Add("start_timing",start.Success ? start.Groups["value"].Value : null,start),
            Add("stop_timing",stop.Success ? stop.Groups["value"].Value : null,stop),
            Add("prn_indication",prn.Success ? prn.Groups["value"].Value.Trim() : null,prn),evidence);
    }
}
