# Rate Limiter API

A production-style **API rate limiter** built with ASP.NET Core (.NET 9). It throttles
incoming traffic to protect downstream services from abuse, brute-force attempts and
resource starvation — the same pattern used by Stripe, GitHub and OpenAI APIs.

Rate limiting is implemented as **middleware (an interceptor)**, not a business endpoint:
it sits in front of every route, decides *allow* or *reject*, and returns
**HTTP 429 (Too Many Requests)** with a `Retry-After` header when a caller exceeds its quota.

> 📐 For the system design, diagram, trade-offs and interview prep, see **[ARCHITECTURE.md](ARCHITECTURE.md)**.

---

## Features

- **4 algorithms**, switchable from config with no code change:
  - **Token Bucket** (default) — steady refill, allows controlled bursts
  - **Fixed Window** — N requests per fixed window
  - **Sliding Window** — smooths the fixed-window edge burst
  - **Concurrency** — caps simultaneous in-flight requests
- **Per-client partitioning** — each client (by `X-Api-Key`, falling back to IP) gets its
  **own independent bucket**, so one noisy caller can't starve everyone else.
- **Standard 429 response** with a `Retry-After` header.
- **Fully config-driven** via the `RateLimiting` section of `appsettings.json`.
- **Integration tests** (xUnit + `WebApplicationFactory`) proving the limit, the
  `Retry-After` header, and per-client isolation.

---

## Project layout

```
Rate_limiter/
├─ Program.cs                       # Composition root; wires middleware pipeline
├─ appsettings.json                 # RateLimiting configuration
├─ Controllers/
│  └─ PingController.cs             # GET /api/ping — a protected demo endpoint
├─ RateLimiting/
│  ├─ RateLimitOptions.cs           # Strongly-typed config (algorithm + all knobs)
│  └─ RateLimiterSetup.cs           # Builds the partitioned limiter from config
└─ tests/RateLimiterApi.Tests/
   └─ RateLimiterTests.cs           # Integration tests
```

---

## Run

```bash
dotnet run
```

The console prints the listening URL (e.g. `http://localhost:5074`).

## Try it

```bash
# Fire 15 rapid requests. With the default token bucket (capacity 10),
# the first ~10 return 200 and the rest return 429.
for i in $(seq 1 15); do
  curl -s -o /dev/null -w "%{http_code}\n" http://localhost:5074/api/ping
done
```

Send an API key to get an independent per-client allowance:

```bash
curl -H "X-Api-Key: customer-123" http://localhost:5074/api/ping
```

## Test

```bash
dotnet test
```

---

## Configuration

All behaviour lives in the `RateLimiting` section of [appsettings.json](appsettings.json):

```jsonc
"RateLimiting": {
  "Algorithm": "TokenBucket",      // FixedWindow | SlidingWindow | TokenBucket | Concurrency
  "PartitionBy": "ApiKey",         // ApiKey (header, falls back to IP) | IpAddress
  "QueueLimit": 0,                 // 0 = reject immediately; >0 = queue then reject

  "TokenBucket": {
    "TokenLimit": 10,              // bucket capacity (max burst)
    "TokensPerPeriod": 5,          // tokens added each period (must be >= 1)
    "ReplenishmentPeriodSeconds": 10
  },
  "Window": {
    "PermitLimit": 10,
    "WindowSeconds": 10,
    "SegmentsPerWindow": 5         // sliding window only
  },
  "Concurrency": {
    "PermitLimit": 5
  }
}
```

Switch algorithm by changing one value — e.g. `"Algorithm": "SlidingWindow"` — and restart.

> **Gotcha:** the framework's token bucket requires `TokensPerPeriod >= 1`. Setting it to `0`
> throws at request time. To simulate "no refill", set a very long `ReplenishmentPeriodSeconds`.

---

## Tech

| | |
|---|---|
| Framework | ASP.NET Core Web API (.NET 9) |
| Rate limiting | Built-in `Microsoft.AspNetCore.RateLimiting` middleware |
| Tests | xUnit + `Microsoft.AspNetCore.Mvc.Testing` |

Production notes (distributed limits with Redis, fail-open vs fail-closed, etc.) are in
**[ARCHITECTURE.md](ARCHITECTURE.md)**.
