using RateLimiterApi.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();

// Configure the rate limiter from the "RateLimiting" section of appsettings.json.
// Algorithm, limits and partitioning are all data-driven — no recompile needed.
builder.Services.AddConfiguredRateLimiter(builder.Configuration);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

// The rate limiter middleware must run before the endpoints it protects.
app.UseRateLimiter();

app.UseAuthorization();

app.MapControllers();

app.Run();

// Exposed so the integration test project can spin up the API with WebApplicationFactory.
public partial class Program { }
