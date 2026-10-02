using ReverseProxyService;


var builder = WebApplication.CreateBuilder(args);


// ==================================================
// 1. HTTP Client
// ==================================================

builder.Services.AddHttpClient();


// ==================================================
// 2. Active OrderService Instances
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
// 5. Background Health Check Worker
// ==================================================

builder.Services.AddHostedService<HealthCheckWorker>();


// ==================================================
// 6. Circuit Breaker
// ==================================================

builder.Services.AddSingleton(
    new CircuitBreakerOptions
    {
        FailureThreshold = 3,
        OpenDuration = TimeSpan.FromSeconds(30)
    }
);

builder.Services.AddSingleton<CircuitBreakerManager>();


// ==================================================
// 7. Instance Selector
// ==================================================

builder.Services.AddSingleton<InstanceSelector>();


var app = builder.Build();


// ==================================================
// 8. Order Gateway Endpoint
// ==================================================

app.Map("/{**path}", async (
    HttpContext context,
    ReverseProxyService proxyService,
    InstanceSelector instanceSelector) =>
{
    // ==================================================
    // 9. Get Incoming Path
    // ==================================================

    var path =
        context.Request.Path;


    Console.WriteLine();
    Console.WriteLine("========== ORDER GATEWAY ==========");

    Console.WriteLine(
        $"Incoming Path: {path}"
    );

    Console.WriteLine(
        $"Method: {context.Request.Method}"
    );


    // ==================================================
    // 10. Validate Order API Path
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
    // 11. Remove /api Prefix
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
        path.Value!.Substring(
            "/api".Length
        );


    // ==================================================
    // 12. Select OrderService Instance
    //
    // InstanceSelector handles:
    //
    // - Active instances
    // - Round robin
    // - Circuit breaker
    // ==================================================

    var selected =
        instanceSelector.Select();


    // ==================================================
    // 13. No Available Instance
    // ==================================================

    if (selected == null)
    {
        Console.WriteLine(
            "No OrderService instance available"
        );

        return Results.StatusCode(
            StatusCodes.Status503ServiceUnavailable
        );
    }


    // ==================================================
    // 14. Get Selected Instance + Circuit
    // ==================================================

    var targetBaseUrl =
        selected.BaseUrl;

    var selectedCircuit =
        selected.Circuit;


    Console.WriteLine(
        $"Selected Instance: {targetBaseUrl}"
    );


    // ==================================================
    // 15. Build Final OrderService URL
    // ==================================================

    var targetUrl =
        targetBaseUrl
        + servicePath
        + context.Request.QueryString;


    Console.WriteLine(
        $"Target URL: {targetUrl}"
    );


    // ==================================================
    // 16. Reverse Proxy
    // ==================================================

    var proxyResponse =
        await proxyService.ForwardAsync(
            context,
            targetUrl
        );


    // ==================================================
    // 17. Record Circuit Breaker Result
    // ==================================================

    if (proxyResponse == null)
    {
        Console.WriteLine(
            $"Circuit failure: {targetBaseUrl}"
        );

        selectedCircuit.RecordFailure();
    }
    else if (
        proxyResponse.StatusCode >= 500
        && proxyResponse.StatusCode <= 599)
    {
        Console.WriteLine(
            $"Circuit failure: {targetBaseUrl} " +
            $"Status: {proxyResponse.StatusCode}"
        );

        selectedCircuit.RecordFailure();
    }
    else
    {
        Console.WriteLine(
            $"Circuit success: {targetBaseUrl}"
        );

        selectedCircuit.RecordSuccess();
    }


    Console.WriteLine(
        "================================="
    );


    return Results.Empty;
});


app.Run();