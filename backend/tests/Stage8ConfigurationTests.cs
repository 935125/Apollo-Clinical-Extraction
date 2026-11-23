using System.Diagnostics;
using System.Text.Json;
using Apollo.Backend.Clinical;
using Apollo.Backend.Configuration;
using Apollo.Backend.Tests.Evaluation;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;

namespace Apollo.Backend.Tests;

public class Stage8ConfigurationTests
{
    private static string Backend => HeldOutBenchmarkRunner.Backend;
    private static string Root => Path.GetFullPath(Path.Combine(Backend, ".."));

    private static async Task<JsonElement> Run(string executable, IEnumerable<string> arguments, string directory, Dictionary<string,string>? environment = null)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in arguments) start.ArgumentList.Add(arg);
        foreach (string key in start.Environment.Keys.Where(k => k.StartsWith("Extraction__", StringComparison.OrdinalIgnoreCase) || k.StartsWith("Foundry__", StringComparison.OrdinalIgnoreCase) || k is "ASPNETCORE_ENVIRONMENT" or "DOTNET_ENVIRONMENT").ToArray()) start.Environment.Remove(key);
        foreach (var item in environment ?? []) start.Environment[item.Key] = item.Value;
        using var child = Process.Start(start)!;
        Task<string> output = child.StandardOutput.ReadToEndAsync(); Task<string> errors = child.StandardError.ReadToEndAsync();
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, child.ExitCode); await errors;
        string payload = (await output).Split('\n').Last(line => line.TrimStart().StartsWith('{'));
        using var json = JsonDocument.Parse(payload);
        string evidence = Path.Combine(Environment.GetEnvironmentVariable("APOLLO_EVALUATION_OUTPUT_ROOT") ?? Path.Combine(Root,"docs/stage8-evidence"), "configuration-runs");
        Directory.CreateDirectory(evidence);
        await File.WriteAllTextAsync(Path.Combine(evidence,Guid.NewGuid().ToString("N")+".json"), JsonSerializer.Serialize(new { command=Path.GetFileName(executable), arguments, explicitRoutingEnvironment=environment, diagnostic=json.RootElement.Clone() }));
        return json.RootElement.Clone();
    }

    [Theory]
    [InlineData("Development", true)]
    [InlineData("Production", false)]
    public async Task CompiledExecutable_AndConfiguration_DefaultToGptFirst(string environmentName, bool ready)
    {
        var result = await Run("dotnet", [Path.Combine(Backend, "bin/Release/net10.0/Apollo.Backend.dll"), "--routing-diagnostics", "--environment", environmentName], Backend);
        Assert.Equal("MicrosoftFoundry", result.GetProperty("requestedPrimaryProvider").GetString());
        Assert.Equal("LocalOnnx", result.GetProperty("fallbackProvider").GetString());
        Assert.Equal(environmentName, result.GetProperty("environment").GetString());
        Assert.Equal(ready, result.GetProperty("configurationReady").GetBoolean());
        Assert.False(result.GetProperty("extractionInferenceExecuted").GetBoolean());
    }

    [Fact]
    public async Task EnvironmentAndCommandLinePrecedence_AreResolvedByActualHost()
    {
        var env = new Dictionary<string,string> { ["DOTNET_ENVIRONMENT"] = "Production", ["ASPNETCORE_ENVIRONMENT"] = "Development", ["Extraction__Provider"] = "LocalOnnx", ["Extraction__AllowDirectLocal"] = "true", ["Extraction__DirectLocalReason"] = "isolated_test" };
        var local = await Run("dotnet", [Path.Combine(Backend, "bin/Release/net10.0/Apollo.Backend.dll"), "--routing-diagnostics"], Backend, env);
        Assert.Equal("Production", local.GetProperty("environment").GetString()); Assert.Equal("LocalOnnx", local.GetProperty("requestedPrimaryProvider").GetString());
        var cli = await Run("dotnet", [Path.Combine(Backend, "bin/Release/net10.0/Apollo.Backend.dll"), "--routing-diagnostics", "--Extraction:Provider", "MicrosoftFoundry"], Backend, env);
        Assert.Equal("MicrosoftFoundry", cli.GetProperty("requestedPrimaryProvider").GetString());
        Assert.False(cli.GetProperty("configurationReady").GetBoolean());
    }

    [Fact]
    public async Task DotnetRun_AndNativeExecutable_UseGptFirstDevelopmentProfile()
    {
        var run = await Run("dotnet", ["run", "--no-build", "-c", "Release", "--project", Path.Combine(Backend,"Apollo.Backend.csproj"), "--", "--routing-diagnostics"], Root);
        Assert.Equal("Development", run.GetProperty("environment").GetString()); Assert.Equal("MicrosoftFoundry", run.GetProperty("requestedPrimaryProvider").GetString());
        var exe = await Run(Path.Combine(Backend,"bin/Release/net10.0/Apollo.Backend.exe"), ["--routing-diagnostics", "--environment", "Development"], Backend);
        Assert.Equal("MicrosoftFoundry", exe.GetProperty("requestedPrimaryProvider").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualWindowsLaunchers_AgreeAndPreserveExplicitProviderOverride(bool batch)
    {
        string program = batch ? "cmd.exe" : "powershell.exe";
        string[] args = batch ? ["/d","/c","Start-Apollo.bat","-ConfigurationOnly"] : ["-NoProfile","-File",Path.Combine(Root,"Start-Apollo.ps1"),"-ConfigurationOnly"];
        var normal = await Run(program, args, Root);
        Assert.Equal("Development", normal.GetProperty("environment").GetString()); Assert.Equal("MicrosoftFoundry", normal.GetProperty("primaryProvider").GetString());
        var local = await Run(program, args, Root, new() { ["Extraction__Provider"] = "LocalOnnx", ["Extraction__AllowDirectLocal"] = "true", ["Extraction__DirectLocalReason"] = "authorized_offline" });
        Assert.Equal("LocalOnnx", local.GetProperty("primaryProvider").GetString()); Assert.False(local.GetProperty("secretsPrinted").GetBoolean());
    }

    [Fact]
    public void LocalDotEnv_IsBelowUserSecretsAndEnvironment_WithoutMutatingProcessSecrets()
    {
        string directory = Path.Combine(Path.GetTempPath(), "apollo-routing-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path.Combine(directory,"backend"));
        try
        {
            File.WriteAllText(Path.Combine(directory,".env"), "Extraction__Provider=LocalOnnx\n");
            File.WriteAllText(Path.Combine(directory,"secrets.json"), "{\"Extraction:Provider\":\"MicrosoftFoundry\"}");
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName="Development", ContentRootPath=Path.Combine(directory,"backend") });
            builder.Configuration.Sources.Clear(); builder.Configuration.AddInMemoryCollection(new Dictionary<string,string?> { ["Extraction:Provider"]="BaseFixture" });
            builder.Configuration.AddJsonFile(new PhysicalFileProvider(directory), "secrets.json", false, false);
            builder.AddLocalDevelopmentDefaults(); Assert.Equal("MicrosoftFoundry", builder.Configuration["Extraction:Provider"]);
            builder.Configuration.AddInMemoryCollection(new Dictionary<string,string?> { ["Extraction:Provider"]="LocalOnnx" });
            Assert.Equal("LocalOnnx",builder.Configuration["Extraction:Provider"]);
        }
        finally { foreach(string file in Directory.GetFiles(directory)) File.Delete(file); Directory.Delete(Path.Combine(directory,"backend")); Directory.Delete(directory); }
    }

    [Fact]
    public void BaseConfigAndOptions_AreGptFirst_WithoutFakeProductionCredentials()
    {
        Assert.Equal("MicrosoftFoundry", new ExtractionOptions().Provider);
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Backend,"appsettings.json")));
        Assert.Equal("MicrosoftFoundry",document.RootElement.GetProperty("Extraction").GetProperty("Provider").GetString());
        Assert.Equal("",document.RootElement.GetProperty("Foundry").GetProperty("Endpoint").GetString());
        Assert.Equal("",document.RootElement.GetProperty("Foundry").GetProperty("DeploymentName").GetString());
    }
}
