using System.Text.Json;
using StackExchange.Redis;
using ReverseProxyService;


var builder = WebApplication.CreateBuilder(args);


// ==================================================
// 1. Redis
// ==================================================

var redis =
    ConnectionMultiplexer.Connect("localhost:6379");

builder.Services.AddSingleton(redis);

builder.Services.AddSingleton(
    redis.GetDatabase()
);


// ==================================================
// 2. Cache Service
// ==================================================

builder.Services.AddSingleton<CacheService>();

// Cache lock for concurrent GET requests
builder.Services.AddSingleton<CacheLockManager>();


// ==================================================
// 3. HTTP Client
// ==================================================

builder.Services.AddHttpClient();


// ==================================================
// 4. Active ProductService Instances
// ==================================================

builder.Services.AddSingleton(
    new ActiveInstancePool(
        new[]
        {
            "http://localhost:5001",
            "http://localhost:5004",
            "http://localhost:5005"
        }
    )
);


// ==================================================
// 5. Recovery Queue
// ==================================================

builder.Services.AddSingleton<RecoveryQueue>();


// ==================================================
// 6. Reverse Proxy Service
// ==================================================

builder.Services.AddSingleton<ReverseProxyService>();


// ==================================================
// 7. Health Check Worker
// ==================================================

builder.Services.AddHostedService<HealthCheckWorker>();


// ==================================================
// 8. Circuit Breaker
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
// 9. Instance Selector
// ==================================================

builder.Services.AddSingleton<InstanceSelector>();


var app = builder.Build();


// ==================================================
// Product Gateway
// ==================================================

app.Map("/{**path}", async (
    HttpContext context,
    ReverseProxyService proxyService,
    CacheService cacheService,
    CacheLockManager cacheLockManager,
    InstanceSelector instanceSelector) =>
{
    var path = context.Request.Path;


    // ==================================================
    // Validate ProductGateway Path
    // ==================================================

    if (!path.StartsWithSegments("/api/products"))
    {
        return Results.NotFound(new
        {
            error = "ProductGateway received an invalid path",
            path = path.ToString()
        });
    }


    // =========================================================
    // GET REQUESTS
    // =========================================================

    if (HttpMethods.IsGet(context.Request.Method))
    {
        // =====================================================
        // 1. Get Current Cache Version
        // =====================================================

        var version =
            await cacheService.GetVersionAsync(
                "products"
            );


        // =====================================================
        // 2. Build Cache Key
        // =====================================================

        var cacheKey =
            cacheService.BuildCacheKey(
                "products",
                version,
                "GET",
                path + context.Request.QueryString
            );


        Console.WriteLine();
        Console.WriteLine("========== PRODUCT GATEWAY ==========");
        Console.WriteLine($"Method:    {context.Request.Method}");
        Console.WriteLine($"Path:      {path}");
        Console.WriteLine($"Cache Key: {cacheKey}");


        // =====================================================
        // 3. FIRST CACHE CHECK
        // =====================================================

        var cachedResponse =
            await cacheService.GetAsync(
                cacheKey
            );


        // =====================================================
        // CACHE HIT
        // =====================================================

        if (cachedResponse != null)
        {
            Console.WriteLine("CACHE HIT");

            var cached =
                JsonSerializer.Deserialize<CachedResponse>(
                    cachedResponse
                );

            if (cached != null)
            {
                // ---------------------------------------------
                // Restore Status Code
                // ---------------------------------------------

                context.Response.StatusCode =
                    cached.StatusCode;


                // ---------------------------------------------
                // Restore Headers
                // ---------------------------------------------

                foreach (var header in cached.Headers)
                {
                    context.Response.Headers[header.Key] =
                        header.Value;
                }


                // ---------------------------------------------
                // Restore Body
                // ---------------------------------------------

                await context.Response.WriteAsync(
                    cached.Body,
                    context.RequestAborted
                );
            }


            Console.WriteLine(
                "===================================="
            );

            return Results.Empty;
        }


        // =====================================================
        // CACHE MISS
        // =====================================================

        Console.WriteLine("CACHE MISS");


        // =====================================================
        // 4. GET PER-KEY SEMAPHORE
        // =====================================================

        var cacheLock =
            cacheLockManager.GetLock(
                cacheKey
            );


        // =====================================================
        // Wait for Permission
        // =====================================================

        await cacheLock.WaitAsync(
            context.RequestAborted
        );


        try
        {
            // =================================================
            // 5. SECOND CACHE CHECK
            // =================================================

            cachedResponse =
                await cacheService.GetAsync(
                    cacheKey
                );


            // =================================================
            // CACHE HIT AFTER WAIT
            // =================================================

            if (cachedResponse != null)
            {
                Console.WriteLine(
                    "CACHE HIT AFTER WAIT"
                );

                var cached =
                    JsonSerializer.Deserialize<CachedResponse>(
                        cachedResponse
                    );

                if (cached != null)
                {
                    // -----------------------------------------
                    // Restore Status Code
                    // -----------------------------------------

                    context.Response.StatusCode =
                        cached.StatusCode;


                    // -----------------------------------------
                    // Restore Headers
                    // -----------------------------------------

                    foreach (var header in cached.Headers)
                    {
                        context.Response.Headers[header.Key] =
                            header.Value;
                    }


                    // -----------------------------------------
                    // Restore Body
                    // -----------------------------------------

                    await context.Response.WriteAsync(
                        cached.Body,
                        context.RequestAborted
                    );
                }


                Console.WriteLine(
                    "===================================="
                );

                return Results.Empty;
            }


            // =================================================
            // 6. STILL CACHE MISS
            //    SELECT PRODUCT SERVICE INSTANCE
            // =================================================

            var servicePath =
                path.Value!.Substring(
                    "/api".Length
                );


            var selected =
                instanceSelector.Select();


            if (selected == null)
            {
                Console.WriteLine(
                    "No ProductService instance available"
                );

                return Results.StatusCode(
                    StatusCodes.Status503ServiceUnavailable
                );
            }


            var targetBaseUrl =
                selected.BaseUrl;

            var selectedCircuit =
                selected.Circuit;


            var targetUrl =
                targetBaseUrl
                + servicePath
                + context.Request.QueryString;


            Console.WriteLine(
                $"Selected Instance: {targetBaseUrl}"
            );

            Console.WriteLine(
                $"Target URL:        {targetUrl}"
            );


            // =================================================
            // 7. CALL PRODUCT SERVICE
            // =================================================

            var proxyResponse =
                await proxyService.ForwardAsync(
                    context,
                    targetUrl
                );


            // =================================================
            // Record Circuit Breaker Result
            // =================================================

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


            // =================================================
            // 8. CACHE COMPLETE RESPONSE
            // =================================================

            if (proxyResponse != null
                && proxyResponse.StatusCode >= 200
                && proxyResponse.StatusCode < 300)
            {
                var serializedResponse =
                    JsonSerializer.Serialize(
                        proxyResponse
                    );


                await cacheService.SetAsync(
                    cacheKey,
                    serializedResponse,
                    TimeSpan.FromMinutes(5)
                );


                Console.WriteLine(
                    $"Cached: {cacheKey}"
                );
            }


            return Results.Empty;
        }
        finally
        {
            // =================================================
            // 9. RELEASE PER-KEY SEMAPHORE
            // =================================================

            cacheLock.Release();
        }
    }


    // =========================================================
    // NON-GET REQUESTS
    // POST / PUT / PATCH / DELETE
    // =========================================================

    var servicePathForWrite =
        path.Value!.Substring(
            "/api".Length
        );


    // =========================================================
    // Select ProductService Instance
    // =========================================================

    var selectedWrite =
        instanceSelector.Select();


    if (selectedWrite == null)
    {
        Console.WriteLine(
            "No ProductService instance available"
        );

        return Results.StatusCode(
            StatusCodes.Status503ServiceUnavailable
        );
    }


    var writeTargetBaseUrl =
        selectedWrite.BaseUrl;

    var selectedWriteCircuit =
        selectedWrite.Circuit;


    var writeTargetUrl =
        writeTargetBaseUrl
        + servicePathForWrite
        + context.Request.QueryString;


    Console.WriteLine();
    Console.WriteLine("========== PRODUCT WRITE ==========");
    Console.WriteLine($"Method: {context.Request.Method}");
    Console.WriteLine($"Path:   {path}");
    Console.WriteLine($"Target: {writeTargetUrl}");


    // =========================================================
    // Forward Write Request
    // =========================================================

    var writeResponse =
        await proxyService.ForwardAsync(
            context,
            writeTargetUrl
        );


    // =========================================================
    // Record Circuit Breaker Result
    // =========================================================

    if (writeResponse == null)
    {
        Console.WriteLine(
            $"Circuit failure: {writeTargetBaseUrl}"
        );

        selectedWriteCircuit.RecordFailure();
    }
    else if (
        writeResponse.StatusCode >= 500
        && writeResponse.StatusCode <= 599)
    {
        Console.WriteLine(
            $"Circuit failure: {writeTargetBaseUrl} " +
            $"Status: {writeResponse.StatusCode}"
        );

        selectedWriteCircuit.RecordFailure();
    }
    else
    {
        Console.WriteLine(
            $"Circuit success: {writeTargetBaseUrl}"
        );

        selectedWriteCircuit.RecordSuccess();
    }


    // =========================================================
    // Successful Write → Bump Cache Version
    // =========================================================

    if (HttpMethods.IsPost(context.Request.Method)
        || HttpMethods.IsPut(context.Request.Method)
        || HttpMethods.IsPatch(context.Request.Method)
        || HttpMethods.IsDelete(context.Request.Method))
    {
        if (writeResponse != null
            && writeResponse.StatusCode >= 200
            && writeResponse.StatusCode < 300)
        {
            await cacheService.BumpVersionAsync(
                "products"
            );

            Console.WriteLine(
                "Product cache version bumped"
            );
        }
    }


    Console.WriteLine(
        "=================================="
    );


    return Results.Empty;
});


app.Run();