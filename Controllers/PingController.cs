using Microsoft.AspNetCore.Mvc;

namespace RateLimiterApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PingController : ControllerBase
{
    // GET /api/ping
    // Protected by the global token-bucket rate limiter (see Program.cs).
    // Call it rapidly to see HTTP 429 once the bucket is empty.
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
