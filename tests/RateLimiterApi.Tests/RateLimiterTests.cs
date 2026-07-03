using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace RateLimiterApi.Tests;

/// <summary>
/// Integration tests that boot the real API in-memory via WebApplicationFactory
/// and hit the protected endpoint over HTTP, asserting real 429 behaviour.
/// </summary>
public class RateLimiterTests
{
    /// <summary>
    /// Build a factory whose rate limiter is configured with the given overrides.
    /// A small token bucket (limit 3, no replenishment during the test window)
    /// makes the limit deterministic.
    /// </summary>
    private static WebApplicationFactory<Program> CreateApi(Dictionary<string, string?> overrides)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(Environments.Development);
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(overrides);
            });
        });
    }

    private static Dictionary<string, string?> TokenBucket(int limit) => new()
    {
        ["RateLimiting:Algorithm"] = "TokenBucket",
        ["RateLimiting:PartitionBy"] = "ApiKey",
        ["RateLimiting:QueueLimit"] = "0",
        ["RateLimiting:TokenBucket:TokenLimit"] = limit.ToString(),
        ["RateLimiting:TokenBucket:TokensPerPeriod"] = "1",           // framework requires >= 1 ...
        ["RateLimiting:TokenBucket:ReplenishmentPeriodSeconds"] = "600" // ... but 600s means no refill within the test
    };

    [Fact]
    public async Task Allows_up_to_the_limit_then_returns_429()
    {
        using var api = CreateApi(TokenBucket(limit: 3));
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "client-A");

        // First 3 requests consume the bucket and succeed.
        for (int i = 1; i <= 3; i++)
        {
            var ok = await client.GetAsync("/api/ping");
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        // 4th request is over the limit.
        var rejected = await client.GetAsync("/api/ping");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
    }

    [Fact]
    public async Task Rejected_response_includes_retry_after_header()
    {
        using var api = CreateApi(TokenBucket(limit: 1));
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "client-B");

        await client.GetAsync("/api/ping");                   // consume the only token
        var rejected = await client.GetAsync("/api/ping");    // over the limit

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.True(rejected.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task Different_api_keys_get_independent_buckets()
    {
        using var api = CreateApi(TokenBucket(limit: 2));

        var clientA = api.CreateClient();
        clientA.DefaultRequestHeaders.Add("X-Api-Key", "tenant-1");

        var clientB = api.CreateClient();
        clientB.DefaultRequestHeaders.Add("X-Api-Key", "tenant-2");

        // Client A exhausts its own bucket.
        Assert.Equal(HttpStatusCode.OK, (await clientA.GetAsync("/api/ping")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await clientA.GetAsync("/api/ping")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await clientA.GetAsync("/api/ping")).StatusCode);

        // Client B is unaffected — proves per-client partition isolation.
        Assert.Equal(HttpStatusCode.OK, (await clientB.GetAsync("/api/ping")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await clientB.GetAsync("/api/ping")).StatusCode);
    }
}
