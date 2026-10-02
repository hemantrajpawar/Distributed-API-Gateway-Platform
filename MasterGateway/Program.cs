using StackExchange.Redis;
using ReverseProxyService;

var builder = WebApplication.CreateBuilder(args);


// ==================================================
// 1. Redis
// ==================================================

var redis =
    ConnectionMultiplexer.Connect("localhost:6379");

builder.Services.AddSingleton(redis);
builder.Services.AddSingleton(redis.GetDatabase());


// ==================================================
// 2. Services
// ==================================================

builder.Services.AddHttpClient();

builder.Services.AddSingleton<RateLimiter>();

builder.Services.AddSingleton<ReverseProxyService>();


var app = builder.Build();


// ==================================================
// MASTER GATEWAY
// ==================================================

app.Map("/{**path}", async (
    HttpContext context,
    RateLimiter rateLimiter,
    ReverseProxyService proxyService) =>
{
    // ==================================================
    // 3. Global Rate Limiting
    // ==================================================

    var ip =
        context.Connection.RemoteIpAddress?.ToString()
        ?? "unknown";

    var rateLimit =
        await rateLimiter.CheckAsync(ip);

    context.Response.Headers["X-RateLimit-Limit"] =
        "100";

    context.Response.Headers["X-RateLimit-Remaining"] =
        rateLimit.Remaining.ToString();

    if (!rateLimit.Allowed)
    {
        return Results.StatusCode(
            StatusCodes.Status429TooManyRequests
        );
    }


    // ==================================================
    // 4. Determine Service Gateway
    // ==================================================

    var path = context.Request.Path;

    string? targetGatewayUrl = null;

    if (path.StartsWithSegments("/api/products"))
    {
        targetGatewayUrl =
            "http://localhost:5006";
    }
    else if (path.StartsWithSegments("/api/orders"))
    {
        targetGatewayUrl =
            "http://localhost:5007";
    }
    else if (path.StartsWithSegments("/api/users"))
    {
        targetGatewayUrl =
            "http://localhost:5008";
    }


    // ==================================================
    // 5. No Route
    // ==================================================

    if (targetGatewayUrl == null)
    {
        return Results.NotFound(new
        {
            error = "No service gateway configured",
            path = path.ToString()
        });
    }


    // ==================================================
    // 6. Build Target URL
    // ==================================================

    var targetUrl =
        targetGatewayUrl
        + path
        + context.Request.QueryString;


    Console.WriteLine();
    Console.WriteLine("========== MASTER GATEWAY ==========");
    Console.WriteLine($"Method:   {context.Request.Method}");
    Console.WriteLine($"Incoming: {path}");
    Console.WriteLine($"Target:   {targetUrl}");
    Console.WriteLine("====================================");


    // ==================================================
    // 7. Reverse Proxy
    // ==================================================

    return await proxyService.ForwardAsync(
        context,
        targetUrl
    );
});


app.Run();