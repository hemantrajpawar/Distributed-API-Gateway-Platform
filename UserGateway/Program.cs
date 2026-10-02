using ReverseProxyService;

var builder = WebApplication.CreateBuilder(args);


// ==================================================
// 1. Register HTTP client
// ==================================================

builder.Services.AddHttpClient();


// ==================================================
// 2. Active UserService instances
// ==================================================

builder.Services.AddSingleton(
    new ActiveInstancePool(
        new[]
        {
            "http://localhost:5021",
            "http://localhost:5024",
            "http://localhost:5025"
        }
    )
);


// ==================================================
// 3. Recovery Queue
// ==================================================

builder.Services.AddSingleton<RecoveryQueue>();


// ==================================================
// 4. Reverse Proxy Service
// ==================================================

builder.Services.AddSingleton<ReverseProxyService>();


// ==================================================
// 5. Background Health Check Worker
// ==================================================

builder.Services.AddHostedService<HealthCheckWorker>();


var app = builder.Build();


// ==================================================
// Round Robin counter
// ==================================================

static int userServiceIndex = 0;


// ==================================================
// 6. User Gateway endpoint
// ==================================================

app.Map("/{**path}", async (
    HttpContext context,
    ReverseProxyService proxyService,
    ActiveInstancePool instancePool) =>
{
    // ==================================================
    // 7. Get incoming path
    // ==================================================

    var path = context.Request.Path;

    Console.WriteLine();
    Console.WriteLine("========== USER GATEWAY ==========");

    Console.WriteLine(
        $"Incoming Path: {path}"
    );

    Console.WriteLine(
        $"Method: {context.Request.Method}"
    );


    // ==================================================
    // 8. Make sure this is a User API request
    // ==================================================

    if (!path.StartsWithSegments("/api/users"))
    {
        return Results.NotFound(new
        {
            error = "UserGateway received an invalid path",
            path = path.ToString()
        });
    }


    // ==================================================
    // 9. Remove /api prefix
    //
    // /api/users
    //      ↓
    // /users
    //
    // /api/users/10
    //      ↓
    // /users/10
    // ==================================================

    var servicePath =
        path.Value!.Substring("/api".Length);


    // ==================================================
    // 10. Get currently healthy instances
    // ==================================================

    var instances =
        instancePool.GetInstances();


    // ==================================================
    // 11. No healthy instances available
    // ==================================================

    if (instances.Length == 0)
    {
        return Results.StatusCode(
            StatusCodes.Status503ServiceUnavailable
        );
    }


    // ==================================================
    // 12. Round Robin Load Balancing
    // ==================================================

    var index =
        Interlocked.Increment(
            ref userServiceIndex
        )
        % instances.Length;


    var targetBaseUrl =
        instances[index];


    Console.WriteLine(
        $"Selected Instance: {targetBaseUrl}"
    );


    // ==================================================
    // 13. Build final UserService URL
    // ==================================================

    var targetUrl =
        targetBaseUrl
        + servicePath
        + context.Request.QueryString;


    Console.WriteLine(
        $"Target URL: {targetUrl}"
    );


    // ==================================================
    // 14. Reverse Proxy
    // ==================================================

    var result =
        await proxyService.ForwardAsync(
            context,
            targetUrl
        );


    Console.WriteLine(
        "================================="
    );


    return result;
});


app.Run();