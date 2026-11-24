using System.Security.Cryptography;
using Apollo.Backend.Emr;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Apollo.Backend.Tests;

public class Stage9SeedTests
{
    [PostgresFact]
    public async Task DevelopmentSeeder_StoresUtcFixturesAndAuthenticatablePasswordHash()
    {
        await using var fixture = new Stage9EmrFixture();
        await fixture.InitializeAsync();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });
        string password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Emr"] = fixture.ConnectionString,
            ["Emr:Seed:Password"] = password
        });

        await EmrAdmin.Run(builder, migrate: false, seed: true);

        var store = new EmrStore(builder.Configuration);
        var authenticated = await new DoctorSessions(store).Login(
            "doctor.krishnan@apollo.local", password, CancellationToken.None);
        Assert.True(authenticated.HasValue);
        var encounters = await store.Encounters(authenticated.Value.Profile.Id, CancellationToken.None);
        Assert.Equal(4, encounters.Count);
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT count(*) FROM emr_encounters
            WHERE encounter_date = '2026-01-28 05:54:00+00'::timestamptz
            """, connection);
        Assert.Equal(2L, await command.ExecuteScalarAsync());
    }
}
