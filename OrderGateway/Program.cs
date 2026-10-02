using ReverseProxyService;

var builder = WebApplication.CreateBuilder(args);


// ==================================================
// 1. HTTP Client
// ==================================================

builder.Services.AddHttpClient();


// ==================================================
// 2. Active OrderService instances
// ==================================================

builder.Services.AddSingleton(
    new ActiveInstancePool(
        new[]
        {
            "http://localhost:5011",
            "http://localhost:5014",
            "http://localhost:5015"
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
// 5. Health Check Worker
// ==================================================

builder.Services.AddHostedService<HealthCheckWorker>();


var app = builder.Build();


// ==================================================
// Round Robin counter
// ==================================================

static int orderServiceIndex = 0;


// ==================================================
// 6. Order Gateway
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
    Console.WriteLine("========== ORDER GATEWAY ==========");

    Console.WriteLine(
        $"Incoming Path: {path}"
    );

    Console.WriteLine(
        $"Method: {context.Request.Method}"
    );


    // ==================================================
    // 8. Make sure this is an Order API request
    // ==================================================

    if (!path.StartsWithSegments("/api/orders"))
    {
        return Results.NotFound(new
        {
            error = "OrderGateway received an invalid path",
            path = path.ToString()
        });
    }


    // ==================================================
    // 9. Remove /api prefix
    //
    // /api/orders
    //       ↓
    // /orders
    //
    // /api/orders/10
    //       ↓
    // /orders/10
    // ==================================================

    var servicePath =
        path.Value!.Substring("/api".Length);


    // ==================================================
    // 10. Get healthy OrderService instances
    // ==================================================

    var instances =
        instancePool.GetInstances();


    if (instances.Length == 0)
    {
        return Results.StatusCode(
            StatusCodes.Status503ServiceUnavailable
        );
    }


    // ==================================================
    // 11. Round Robin Load Balancing
    // ==================================================

    var index =
        Interlocked.Increment(
            ref orderServiceIndex
        )
        % instances.Length;


    var targetBaseUrl =
        instances[index];


    Console.WriteLine(
        $"Selected Instance: {targetBaseUrl}"
    );


    // ==================================================
    // 12. Build final OrderService URL
    // ==================================================

    var targetUrl =
        targetBaseUrl
        + servicePath
        + context.Request.QueryString;


    Console.WriteLine(
        $"Target URL: {targetUrl}"
    );


    // ==================================================
    // 13. Reverse Proxy
    // ==================================================

    return await proxyService.ForwardAsync(
        context,
        targetUrl
    );
});


app.Run();