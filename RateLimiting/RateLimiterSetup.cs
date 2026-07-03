using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace RateLimiterApi.RateLimiting;

/// <summary>
/// Central place that translates <see cref="RateLimitOptions"/> into a configured
/// ASP.NET Core rate limiter. Two ideas are combined here:
///
///   1. A GLOBAL partitioned limiter — every request is bucketed by client identity
///      (API key or IP), so each client gets its own independent allowance and one
///      abusive caller cannot exhaust the limit for everyone else.
///
///   2. Algorithm is chosen at runtime from configuration (token bucket / fixed /
///      sliding window / concurrency) without touching code.
/// </summary>
public static class RateLimiterSetup
{
    public static IServiceCollection AddConfiguredRateLimiter(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RateLimitOptions>(configuration.GetSection(RateLimitOptions.SectionName));

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // GlobalLimiter applies to every request. We partition by client identity
            // and build the correct limiter for the configured algorithm. Options are
            // read from DI per request (via IOptionsMonitor) so the limiter always sees
            // the final merged configuration rather than a snapshot bound at startup.
            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var options = context.RequestServices
                    .GetRequiredService<IOptionsMonitor<RateLimitOptions>>().CurrentValue;
                var partitionKey = ResolvePartitionKey(context, options.PartitionBy);
                return CreateLimiter(partitionKey, options);
            });

            // Emit a Retry-After header so well-behaved clients know when to retry.
            limiter.OnRejected = async (context, token) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)retryAfter.TotalSeconds).ToString();
                }

                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await context.HttpContext.Response.WriteAsync(
                    "Rate limit exceeded. Please retry later.", token);
            };
        });

        return services;
    }

    /// <summary>
    /// Derives the partition key for a request. API-key strategy prefers the
    /// X-Api-Key header and falls back to the remote IP when absent, so anonymous
    /// callers are still bucketed (by IP) rather than sharing one global bucket.
    /// </summary>
    private static string ResolvePartitionKey(HttpContext context, PartitionStrategy strategy)
    {
        if (strategy == PartitionStrategy.ApiKey &&
            context.Request.Headers.TryGetValue("X-Api-Key", out var apiKey) &&
            !string.IsNullOrWhiteSpace(apiKey))
        {
            return $"key:{apiKey}";
        }

        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return $"ip:{ip}";
    }

    private static RateLimitPartition<string> CreateLimiter(string partitionKey, RateLimitOptions options)
    {
        return options.Algorithm switch
        {
            RateLimitAlgorithm.TokenBucket => RateLimitPartition.GetTokenBucketLimiter(
                partitionKey, _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = options.TokenBucket.TokenLimit,
                    TokensPerPeriod = options.TokenBucket.TokensPerPeriod,
                    ReplenishmentPeriod = TimeSpan.FromSeconds(options.TokenBucket.ReplenishmentPeriodSeconds),
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = options.QueueLimit,
                    AutoReplenishment = true
                }),

            RateLimitAlgorithm.FixedWindow => RateLimitPartition.GetFixedWindowLimiter(
                partitionKey, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = options.Window.PermitLimit,
                    Window = TimeSpan.FromSeconds(options.Window.WindowSeconds),
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = options.QueueLimit
                }),

            RateLimitAlgorithm.SlidingWindow => RateLimitPartition.GetSlidingWindowLimiter(
                partitionKey, _ => new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = options.Window.PermitLimit,
                    Window = TimeSpan.FromSeconds(options.Window.WindowSeconds),
                    SegmentsPerWindow = options.Window.SegmentsPerWindow,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = options.QueueLimit
                }),

            RateLimitAlgorithm.Concurrency => RateLimitPartition.GetConcurrencyLimiter(
                partitionKey, _ => new ConcurrencyLimiterOptions
                {
                    PermitLimit = options.Concurrency.PermitLimit,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = options.QueueLimit
                }),

            _ => throw new ArgumentOutOfRangeException(
                nameof(options.Algorithm), options.Algorithm, "Unsupported rate limit algorithm.")
        };
    }
}
