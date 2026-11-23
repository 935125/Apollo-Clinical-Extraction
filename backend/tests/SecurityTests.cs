using Apollo.Backend.Security;

namespace Apollo.Backend.Tests;

public class SecurityTests
{
    [Fact]
    public void Validator_Rejects_Aadhaar_Number()
    {
        string note = "Patient presented with fever. Aadhaar: 1234 5678 9012.";
        var ex = Assert.Throws<PIILeakageException>(() => ZeroLeakageValidator.Validate(note));
        Assert.Contains(ex.Flagged, f => f.Type == "AADHAAR");
    }

    [Fact]
    public void Validator_Rejects_Abha_Id()
    {
        string note = "Patient ABHA id is 12-3456-7890-1234.";
        var ex = Assert.Throws<PIILeakageException>(() => ZeroLeakageValidator.Validate(note));
        Assert.Contains(ex.Flagged, f => f.Type == "ABHA");
    }

    [Fact]
    public void Validator_Rejects_Pan_Card()
    {
        string note = "Billing tax PAN ABCDE1234F provided.";
        var ex = Assert.Throws<PIILeakageException>(() => ZeroLeakageValidator.Validate(note));
        Assert.Contains(ex.Flagged, f => f.Type == "PAN");
    }

    [Fact]
    public void Validator_Rejects_Email_Address()
    {
        string note = "Patient email is test.user@apollohospital.in for reports.";
        var ex = Assert.Throws<PIILeakageException>(() => ZeroLeakageValidator.Validate(note));
        Assert.Contains(ex.Flagged, f => f.Type == "EMAIL");
    }

    [Fact]
    public void Validator_Rejects_Phone_Number()
    {
        string note = "Emergency contact: +91 9876543210.";
        var ex = Assert.Throws<PIILeakageException>(() => ZeroLeakageValidator.Validate(note));
        Assert.Contains(ex.Flagged, f => f.Type == "PHONE");
    }

    [Fact]
    public void Validator_Rejects_Age_Over_89()
    {
        string note = "Patient is a 92 yo male admitted with fatigue.";
        var ex = Assert.Throws<PIILeakageException>(() => ZeroLeakageValidator.Validate(note));
        Assert.Contains(ex.Flagged, f => f.Type == "AGE>89");
    }

    [Fact]
    public void Validator_Allows_Age_Under_90()
    {
        string note = "Patient is a 65 yo male admitted with chest pain.";
        // Should not throw
        ZeroLeakageValidator.Validate(note);
    }

    [Fact]
    public void Validator_Allows_Clinical_Eponyms()
    {
        string note = "History of Parkinson's disease and Hashimoto thyroiditis. Seen at St. Jude partner center.";
        // Eponyms should not trigger false positive name or facility rejections
        ZeroLeakageValidator.Validate(note);
    }

    [Fact]
    public void Validator_Scrub_Masks_Identifiers()
    {
        string note = "Email: patient@hospital.com, Phone: +91 9876543210, Aadhaar: 1234 5678 9012";
        string scrubbed = ZeroLeakageValidator.Scrub(note);

        Assert.DoesNotContain("patient@hospital.com", scrubbed);
        Assert.DoesNotContain("9876543210", scrubbed);
        Assert.DoesNotContain("1234 5678 9012", scrubbed);
        Assert.Contains("[EMAIL]", scrubbed);
        Assert.Contains("[PHONE]", scrubbed);
    }
}
