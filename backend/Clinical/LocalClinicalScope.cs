using System.Text.RegularExpressions;

namespace Apollo.Backend.Clinical;

public static class LocalClinicalScope
{
    internal const string Actions = @"\b(?:start(?:ed)?|initiat\w*|prescrib\w*|add(?:ed)?|giv(?:e|en)|administer\w*|continu\w*|stop(?:ped)?|discontinu\w*|withh(?:old|eld)|hold|held|avoid|increas\w*|decreas\w*|order\w*|obtain|repeat|check|send)\b";
    public static bool Has(string text,string pattern) => Regex.IsMatch(text,pattern,RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public static (int Start,int End) Sentence(string source,int start,int end)
    {
        bool Break(int i) => source[i] is '\r' or '\n' or ';' or '!' or '?' ||
            source[i]=='.' && (i+1==source.Length || char.IsWhiteSpace(source[i+1]));
        int left=start,right=end;
        while(left>0 && !Break(left-1)) left--;
        while(right<source.Length && !Break(right)) right++;
        return (left,right);
    }
    public static (int Start,int End) Attributes(string source,LocalEntitySpan entity,IReadOnlyList<LocalEntitySpan> all)
    {
        var (left,right)=Sentence(source,entity.Start,entity.End);
        var previous=all.Where(e=>e.End<=entity.Start && e.End>left).MaxBy(e=>e.End);
        if(previous!=null)
        {
            string gap=source[previous.End..entity.Start];
            var markers=Regex.Matches(gap,@"\b(?:and|but|while|whereas)\b|[,+]",RegexOptions.IgnoreCase);
            left=markers.Count>0 ? previous.End+markers[^1].Index+markers[^1].Length : entity.Start;
            var action=Regex.Matches(gap,Actions,RegexOptions.IgnoreCase);
            if(action.Count>0) left=previous.End+action[^1].Index;
        }
        var next=all.Where(e=>e.Start>=entity.End && e.Start<right).MinBy(e=>e.Start);
        if(next!=null)
        {
            right=next.Start;
            var actions=Regex.Matches(source[entity.End..right],Actions,RegexOptions.IgnoreCase);
            if(actions.Count>0) right=entity.End+actions[0].Index;
        }
        return (left,right);
    }
    public static string AssertionScope(string source,LocalEntitySpan entity,IReadOnlyList<LocalEntitySpan> all)
    {
        var range=Attributes(source,entity,all);
        var sentence=Sentence(source,entity.Start,entity.End);
        string local=source[range.Start..range.End];
        if(range.Start>sentence.Start)
        {
            // Coordinated lists may inherit the leading action/negation, never another drug's dose.
            string prefix=source[sentence.Start..range.Start];
            var cues=Regex.Matches(prefix,Actions+@"|\b(?:not taking|no|denies|previously|historical|allergic to|if|consider)\b",RegexOptions.IgnoreCase);
            if(cues.Count>0 && !Has(source[range.Start..entity.Start],Actions)) local=cues[^1].Value+" "+local;
        }
        return local;
    }
}
