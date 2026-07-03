# Rate_limiter

An ASP.NET Core (.NET 9) Web API demonstrating **token bucket** rate limiting
using the built-in `Microsoft.AspNetCore.RateLimiting` middleware.

## How the token bucket works

A bucket holds up to `TokenLimit` tokens. Each request spends **1 token**.
Tokens refill by `TokensPerPeriod` every `ReplenishmentPeriod`. When the bucket
is empty, requests are rejected with **HTTP 429 (Too Many Requests)** and a
`Retry-After` header. This allows short bursts (up to the bucket size) while
capping the long-run average request rate.

Current settings (in [Program.cs](Program.cs)):

| Setting | Value | Meaning |
|---|---|---|
| `TokenLimit` | 10 | max burst / bucket capacity |
| `TokensPerPeriod` | 5 | tokens added each period |
| `ReplenishmentPeriod` | 10s | how often tokens refill |
| `QueueLimit` | 0 | reject immediately, don't queue |

So: up to 10 requests instantly, then ~5 requests every 10 seconds.

## Run

```bash
dotnet run
```

## Test

```bash
# Fire 15 requests quickly — the first ~10 return 200, the rest return 429.
for i in $(seq 1 15); do
  curl -s -o /dev/null -w "%{http_code}\n" http://localhost:5xxx/api/ping
done
```

(Replace `5xxx` with the port printed by `dotnet run`.)

## Endpoint

- `GET /api/ping` → `{ "message": "pong", "timeUtc": "..." }`