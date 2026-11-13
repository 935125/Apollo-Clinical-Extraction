using System.Text.Json;
using Cx.Domain.Enums;
using Shouldly;
using Xunit;

namespace Cx.Domain.Tests;

public class DomainEnumParityTests
{
    [Fact]
    public void MedicationAssertionStatus_MatchesAuthoritativeJson()
    {
        // Navigate from bin/Debug/net10.0 to root directory
        var contractPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../../contracts/enums.v1.json"));
        File.Exists(contractPath).ShouldBeTrue($"Authoritative contract must exist at {contractPath}");

        var json = File.ReadAllText(contractPath);
        using var doc = JsonDocument.Parse(json);
        var authoritativeStatuses = doc.RootElement
            .GetProperty("enums")
            .GetProperty("medication_assertion_status")
            .EnumerateArray()
            .Select(x => x.GetString())
            .ToList();

        var codeValues = Enum.GetNames<MedicationAssertionStatus>()
            .Select(ToSnakeCase)
            .ToList();

        codeValues.Count.ShouldBe(authoritativeStatuses.Count);
        foreach (var status in authoritativeStatuses)
        {
            codeValues.ShouldContain(status);
        }
    }

    private static string ToSnakeCase(string str) =>
        string.Concat(str.Select((x, i) => i > 0 && char.IsUpper(x) ? "_" + x.ToString() : x.ToString())).ToLower();
}
