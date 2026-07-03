using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();

// ---------------------------------------------------------------------------
// Token bucket rate limiting
// ---------------------------------------------------------------------------
// A bucket holds up to TokenLimit tokens. Every request spends 1 token.
// Tokens refill by TokensPerPeriod every ReplenishmentPeriod. When the bucket
// is empty, further requests are rejected with HTTP 429 (Too Many Requests).
// This allows short bursts (up to the bucket size) while capping the long-run
// average rate.
const string TokenBucketPolicy = "token-bucket";

builder.Services.AddRateLimiter(options =>
{
    // Return 429 instead of the default 503 when a request is rejected.
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddTokenBucketLimiter(policyName: TokenBucketPolicy, bucket =>
    {
        bucket.TokenLimit = 10;                                   // bucket capacity (max burst)
        bucket.TokensPerPeriod = 5;                               // tokens added each period
        bucket.ReplenishmentPeriod = TimeSpan.FromSeconds(10);    // how often tokens are added
        bucket.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        bucket.QueueLimit = 0;                                    // 0 = reject immediately, don't queue
        bucket.AutoReplenishment = true;
    });

    // Add a Retry-After header so clients know when to try again.
    options.OnRejected = async (context, cancellationToken) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter =
                ((int)retryAfter.TotalSeconds).ToString();
        }

        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await context.HttpContext.Response.WriteAsync(
            "Rate limit exceeded. Please try again later.", cancellationToken);
    };
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

// The rate limiter middleware must run before endpoints are executed.
app.UseRateLimiter();

app.UseAuthorization();

// Apply the token bucket policy to all controller endpoints.
app.MapControllers().RequireRateLimiting(TokenBucketPolicy);

app.Run();
