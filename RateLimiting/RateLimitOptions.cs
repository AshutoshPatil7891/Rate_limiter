namespace RateLimiterApi.RateLimiting;

/// <summary>
/// Which rate-limiting algorithm the API should use. Selected at runtime via
/// the "RateLimiting:Algorithm" configuration key (appsettings.json / env var).
/// </summary>
public enum RateLimitAlgorithm
{
    /// <summary>N permits per fixed time window. Simple, but allows a 2x burst at window edges.</summary>
    FixedWindow,

    /// <summary>Fixed window split into segments to smooth out the edge-burst problem.</summary>
    SlidingWindow,

    /// <summary>Bucket of tokens refilled at a steady rate. Allows controlled bursts. Default.</summary>
    TokenBucket,

    /// <summary>Caps the number of requests running at the same time (not per-time-unit).</summary>
    Concurrency
}

/// <summary>
/// Strongly-typed binding for the "RateLimiting" section of configuration.
/// Every knob is externalised so behaviour can change without a recompile.
/// </summary>
public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimiting";

    public RateLimitAlgorithm Algorithm { get; set; } = RateLimitAlgorithm.TokenBucket;

    /// <summary>
    /// How requests are grouped into independent limits. "ApiKey" reads the
    /// X-Api-Key header and falls back to client IP; "IpAddress" always uses IP.
    /// Each partition gets its own bucket, so one noisy client cannot starve others.
    /// </summary>
    public PartitionStrategy PartitionBy { get; set; } = PartitionStrategy.ApiKey;

    /// <summary>Requests that exceed the limit are queued (up to this many) instead of rejected. 0 = reject immediately.</summary>
    public int QueueLimit { get; set; } = 0;

    // --- Token bucket ---
    public TokenBucketSettings TokenBucket { get; set; } = new();

    // --- Fixed / sliding window ---
    public WindowSettings Window { get; set; } = new();

    // --- Concurrency ---
    public ConcurrencySettings Concurrency { get; set; } = new();
}

public enum PartitionStrategy
{
    ApiKey,
    IpAddress
}

public sealed class TokenBucketSettings
{
    /// <summary>Bucket capacity — the largest instantaneous burst allowed.</summary>
    public int TokenLimit { get; set; } = 10;

    /// <summary>Tokens added to the bucket each replenishment period.</summary>
    public int TokensPerPeriod { get; set; } = 5;

    /// <summary>How often (seconds) tokens are replenished.</summary>
    public int ReplenishmentPeriodSeconds { get; set; } = 10;
}

public sealed class WindowSettings
{
    /// <summary>Permitted requests per window.</summary>
    public int PermitLimit { get; set; } = 10;

    /// <summary>Window length in seconds.</summary>
    public int WindowSeconds { get; set; } = 10;

    /// <summary>Number of segments the window is divided into (sliding window only).</summary>
    public int SegmentsPerWindow { get; set; } = 5;
}

public sealed class ConcurrencySettings
{
    /// <summary>Maximum number of requests allowed to execute simultaneously.</summary>
    public int PermitLimit { get; set; } = 5;
}
