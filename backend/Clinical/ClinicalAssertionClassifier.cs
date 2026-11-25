using System.Text.RegularExpressions;

namespace Apollo.Backend.Clinical;

public sealed record LocalAssertion(string Assertion,string LegacyStatus,string Temporality,string Action);

/// <summary>Explicit cues precede section defaults. Numeric result detection is entity-local, never an appointment time.</summary>
public static class ClinicalAssertionClassifier
{
    public static LocalAssertion Classify(string source,LocalEntitySpan entity,IReadOnlyList<LocalEntitySpan> entities,IReadOnlyList<ClinicalSection> sections)
    {
        string scope=LocalClinicalScope.AssertionScope(source,entity,entities);
        string section=new ContextEngine().DetermineSection(entity.Start,sections);
        bool H(string p)=>LocalClinicalScope.Has(scope,p);
        bool medication=entity.Category=="MEDICATION";
        LocalAssertion A(string assertion,string legacy,string temporal="current",string action="none")=>new(assertion,legacy,temporal,action);
        if(section=="FAMILY_HISTORY" || H(@"\b(?:mother|father|sister|brother|family member)\b"))
            return A("other_experiencer","uncertain","unspecified");
        if(H(@"\b(?:unclear|uncertain|unknown whether|possibly|may have|cannot confirm)\b")) return A("uncertain","uncertain","unspecified");
        if(H(@"\b(?:not only|no change|without delay)\b"))
            scope=Regex.Replace(scope,@"\b(?:not only|no change|without delay)\b","",RegexOptions.IgnoreCase);
        bool negated=H(@"\b(?:not taking|not receiving|not prescribed|no longer taking|denies|denied|never|avoid|no repeat|not required|not indicated|not planned|not ordered|not performed|no\s+(?:known\s+)?(?:drug\s+)?allerg\w*|without\s+allerg\w*)\b") ||
            H(@"\bno\b[^.;\n]*\b(?:required|indicated|allerg\w*)\b");
        if(negated) return A("negated","negated","unspecified");
        if(medication && (section=="ALLERGIES" || H(@"\b(?:allerg\w*|hypersensitiv\w*)\b"))) return A("allergy","allergy","unspecified","allergy");
        if(section=="EDUCATION" || H(@"\b(?:educational|patient education|example|counsell?ing)\b")) return A("educational","uncertain","hypothetical");
        if(H(@"\b(?:if|unless|depending on|in case of|consider|differential|hypothetical)\b")) return A("conditional","conditional","conditional");
        bool old=section is "PAST_MEDICAL_HISTORY" or "MEDICATION_HISTORY" or "INVESTIGATION_HISTORY" ||
            H(@"\b(?:previously|prior|last year|last month|years? ago|months? ago|history of)\b");
        if(medication)
        {
            if(H(@"\b(?:completed|finished)\b[^.;\n]*\b(?:course|treatment|therapy)\b|\b(?:course|treatment|therapy)\b[^.;\n]*\bcompleted\b|\bsuccessfully treated\b")) return A("completed_course","historical","past");
            if(old) return A("historical","historical","past");
            if(H(@"\b(?:withheld|withhold|hold|held)\b")) return A("held","held","current","hold");
            if(section=="DISCONTINUED_MEDICATIONS" || H(@"\b(?:discontinu\w*|stop(?:ped)?|ceased)\b")) return A("discontinued","discontinued","past","stop");
            if(H(@"\b(?:planned|planning|will start)\b") || section=="FOLLOW_UP_PLAN") return A("planned","conditional","future");
            if(H(@"\b(?:continu\w*)\b")) return A("continued","current_medication","current","continue");
            if(H(@"\b(?:start(?:ed)?|initiat\w*|prescrib\w*|add(?:ed)?|give|administer|increase|decrease)\b"))
                return A("new","new_order","current", H(@"\bincrease\b") ? "increase" : H(@"\bdecrease\b") ? "decrease" : "start");
            if(section=="HOSPITAL_COURSE" || H(@"\b(?:received|administered|was given)\b")) return A("historical","historical","past");
            if(section is "MEDICATIONS" or "CHEMOTHERAPY" or "CURRENT_TREATMENT" || H(@"\b(?:takes|taking|on)\b") ||
                H(@"\b\d+(?:\.\d+)?\s*(?:mg|mcg|g|mL|units?)\b")) return A("current","current_medication");
            return A("uncertain","uncertain","unspecified");
        }
        if(old) return A("historical","historical","past");
        var range=LocalClinicalScope.Attributes(source,entity,entities);
        string after=source[entity.End..range.End];
        bool result=LocalClinicalScope.Has(after,@"^\s*[:=]?\s*\d+(?:[.,]\d+)*(?:\s*(?:g/dL|mg/dL|mmol/L|mEq/L|/uL|%|U/L))?\b|\b(?:showed|shows|grew|confirmed|revealed|result(?:ed)?|was normal|were normal)\b");
        if(result && !H(@"\b(?:order|obtain|check|send|report|attend|repeat)\b")) return A("result_documented","historical","past");
        if(H(@"\b(?:performed|done|collected|completed|underwent)\b") || section=="INVESTIGATIONS_PERFORMED") return A("completed","historical","past");
        if(H(@"\b(?:report|attend|appointment|scheduled|booked|return)\b") ||
            section=="FOLLOW_UP_PLAN" && H(@"\b\d{1,2}/\d{1,2}/\d{4}\b")) return A("scheduled","new_order","future");
        if(H(@"\b(?:order(?:ed)?|obtain|check|send|advise(?:d)?|repeat|measure)\b")) return A("newly_ordered","new_order","future");
        if(H(@"\b(?:plan(?:ned)?|next week|next month|tomorrow)\b") || section is "FOLLOW_UP_PLAN" or "INVESTIGATIONS_PLANNED" or "ASSESSMENT_PLAN") return A("planned","new_order","future");
        if(section=="HOSPITAL_COURSE") return A("historical","historical","past");
        return A("uncertain","uncertain","unspecified");
    }
}
