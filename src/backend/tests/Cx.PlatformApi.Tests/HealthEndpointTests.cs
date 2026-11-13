using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;
using Xunit;

namespace Cx.PlatformApi.Tests;

public class HealthEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public HealthEndpointTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task HealthLive_ReturnsOkWithHealthyStatus()
    {
        var response = await _client.GetAsync("/health/live");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        
        var content = await response.Content.ReadAsStringAsync();
        content.ShouldContain("\"status\":\"Healthy\"");
        content.ShouldContain("self");
    }

    [Fact]
    public async Task HealthReady_ReturnsOkWithReadyStatus()
    {
        var response = await _client.GetAsync("/health/ready");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        content.ShouldContain("\"status\":\"Healthy\"");
        content.ShouldContain("database");
    }
}

