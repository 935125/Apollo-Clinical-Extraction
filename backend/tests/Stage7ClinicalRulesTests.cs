using Apollo.Backend.Clinical;
using Apollo.Backend.Nlp;

namespace Apollo.Backend.Tests;

public class Stage7ClinicalRulesTests
{
    [Theory]
    [InlineData("Obtain MRI of the cervical spine tomorrow.", "MRI", "MRI of the cervical spine")]
    [InlineData("Order low-dose CT abdomen in 5 days.", "CT", "low-dose CT abdomen")]
    [InlineData("Order Troponin T today.", "Troponin", "Troponin T")]
    [InlineData("Order MRI today.", "MRI", "MRI")]
    public void QualifiersRemainSourceSubstrings(string source, string original, string expected)
    {
        int start = source.IndexOf(original, StringComparison.Ordinal);
        var result = InvestigationMentionRules.Qualify(source, new("INVESTIGATION", start, start + original.Length, original, 1));
        Assert.Equal(expected, result.Text); Assert.Equal(expected, source[result.StartChar..result.EndChar]);
    }

    [Fact]
    public void ProductiveInvestigationGrammar_DoesNotTurnTherapyOrThresholdsIntoOrders()
    {
        Assert.Empty(InvestigationMentionRules.ProductiveMentions("Order home oxygen and physiotherapy."));
        Assert.True(InvestigationMentionRules.IsThreshold("Start therapy if serum marker remains elevated."));
        Assert.False(InvestigationMentionRules.IsThreshold("Order serum marker if symptoms persist."));
        Assert.Equal(["wound culture", "random plasma glucose"], InvestigationMentionRules.ProductiveMentions("Obtain wound culture and random plasma glucose tomorrow.").Select(s => s.Text));
    }
}
