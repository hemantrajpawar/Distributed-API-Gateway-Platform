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
// 4. Active ProductService instances
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


var app = builder.Build();


// ==================================================
// Round Robin counter
// ==================================================

static int productServiceIndex = 0;


// ==================================================
// Product Gateway
// ==================================================

app.Map("/{**path}", async (
    HttpContext context,
    ReverseProxyService proxyService,
    ActiveInstancePool instancePool,
    CacheService cacheService,
    CacheLockManager cacheLockManager) =>
{
    var path = context.Request.Path;


    // ==================================================
    // Validate ProductGateway path
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
    // GET → CACHE
    // =========================================================

    if (HttpMethods.IsGet(context.Request.Method))
    {
        // =====================================================
        // 1. Get current version
        // =====================================================

        var version =
            await cacheService.GetVersionAsync(
                "products"
            );


        // =====================================================
        // 2. Build cache key
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
                // Restore status code
                // ---------------------------------------------

                context.Response.StatusCode =
                    cached.StatusCode;


                // ---------------------------------------------
                // Restore headers
                // ---------------------------------------------

                foreach (var header in cached.Headers)
                {
                    context.Response.Headers[header.Key] =
                        header.Value;
                }


                // ---------------------------------------------
                // Restore body
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


        Console.WriteLine("CACHE MISS");


        // =====================================================
        // 4. GET PER-KEY SEMAPHORE
        // =====================================================

        var cacheLock =
            cacheLockManager.GetLock(
                cacheKey
            );


        // =====================================================
        // Wait for permission
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
                    // Restore status code
                    // -----------------------------------------

                    context.Response.StatusCode =
                        cached.StatusCode;


                    // -----------------------------------------
                    // Restore headers
                    // -----------------------------------------

                    foreach (var header in cached.Headers)
                    {
                        context.Response.Headers[header.Key] =
                            header.Value;
                    }


                    // -----------------------------------------
                    // Restore body
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
            // 6. STILL MISS
            //    SELECT PRODUCT SERVICE INSTANCE
            // =================================================

            var servicePath =
                path.Value!.Substring(
                    "/api".Length
                );


            var instances =
                instancePool.GetInstances();


            if (instances.Length == 0)
            {
                return Results.StatusCode(
                    StatusCodes.Status503ServiceUnavailable
                );
            }


            // =================================================
            // Round Robin
            // =================================================

            var index =
                Interlocked.Increment(
                    ref productServiceIndex
                )
                % instances.Length;


            var targetBaseUrl =
                instances[index];


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
    // =========================================================

    var servicePathForWrite =
        path.Value!.Substring(
            "/api".Length
        );


    var writeInstances =
        instancePool.GetInstances();


    if (writeInstances.Length == 0)
    {
        return Results.StatusCode(
            StatusCodes.Status503ServiceUnavailable
        );
    }


    // =========================================================
    // Round Robin for write request
    // =========================================================

    var writeIndex =
        Interlocked.Increment(
            ref productServiceIndex
        )
        % writeInstances.Length;


    var writeTargetBaseUrl =
        writeInstances[writeIndex];


    var writeTargetUrl =
        writeTargetBaseUrl
        + servicePathForWrite
        + context.Request.QueryString;


    Console.WriteLine();
    Console.WriteLine("========== PRODUCT WRITE ==========");
    Console.WriteLine($"Method: {context.Request.Method}");
    Console.WriteLine($"Path:   {path}");
    Console.WriteLine(
        $"Target: {writeTargetUrl}"
    );


    // =========================================================
    // Forward write request
    // =========================================================

    var writeResponse =
        await proxyService.ForwardAsync(
            context,
            writeTargetUrl
        );


    // =========================================================
    // Successful write → bump cache version
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