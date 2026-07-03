using Microsoft.AspNetCore.Mvc;

namespace RateLimiterApi.Controllers;

/// <summary>
/// A trivial protected endpoint used to demonstrate the rate limiter.
/// The limiter itself is applied globally as middleware (see Program.cs /
/// RateLimiterSetup) — a Rate Limiter is an interceptor, not a business endpoint.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class PingController : ControllerBase
{
    // GET /api/ping
    // Call it rapidly to see HTTP 429 (Too Many Requests) once the limit is hit.
    // Send an "X-Api-Key" header to get an independent per-client allowance.
    [HttpGet]
    public IActionResult Get()
    {
        return Ok(new
        {
            message = "pong",
            timeUtc = DateTime.UtcNow
        });
    }
}
