
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

var redis =
    ConnectionMultiplexer.Connect(
        "localhost:6379"
    );

// Register IHttpClientFactory
builder.Services.AddHttpClient();

builder.Services.AddSingleton(
    new ActiveInstancePool(
        new[]
        {
            "http://localhost:5001",
            "http://localhost:5002",
            "http://localhost:5003"
        }
    )
);

builder.Services.AddSingleton(redis);

builder.Services.AddSingleton(
    redis.GetDatabase()
);

builder.Services.AddSingleton<RecoveryQueue>();

builder.Services.AddHostedService<HealthCheckWorker>();

builder.Services.AddSingleton<RateLimiter>();

var app = builder.Build();

int userServiceIndex = -1;

// ======================================================
// Gateway - Reverse Proxy
// ======================================================

app.Map("/{**path}", async (
    HttpContext context,
    IHttpClientFactory httpClientFactory ,
    ActiveInstancePool instancePool,
    RateLimiter rateLimiter) =>
    {
        // getting IP
        var ip =
        context.Connection.RemoteIpAddress?
            .ToString()
        ?? "unknown";

        // just to check IP comin or not..
        Console.WriteLine($"______________________________________________RATE LIMIT IP = {ip}");

        // check the allowance
        var rateLimit =
        await rateLimiter.CheckAsync(ip);

        context.Response.Headers["X-RateLimit-Limit"] = "100";

        context.Response.Headers["X-RateLimit-Remaining"] =
            rateLimit.Remaining.ToString();

        if (!rateLimit.Allowed)
        {
            return Results.StatusCode(
                StatusCodes.Status429TooManyRequests  
            );
        }
        // --------------------------------------------------
        // 1. Get incoming request path
        // --------------------------------------------------

        var path = context.Request.Path;


        // --------------------------------------------------
        // 2. Decide which backend service should receive
        //    this request
        // --------------------------------------------------

        string? targetBaseUrl = null;

        if (path.StartsWithSegments("/api/users"))
        {
            var instances = instancePool.GetInstances();

            if (instances.Length == 0)
            {
                return Results.Problem(
                    title: "No healthy UserService instances",
                    statusCode: StatusCodes.Status503ServiceUnavailable
                ); 
            }

            var index =
                Interlocked.Increment(ref userServiceIndex)
                % instances.Length;

            targetBaseUrl = instances[index];
        }
        else if (path.StartsWithSegments("/api/products"))
        {
            targetBaseUrl = "http://localhost:5004";
        }
        else if (path.StartsWithSegments("/api/orders"))
        {
            targetBaseUrl = "http://localhost:5005";
        }


        // --------------------------------------------------
        // 3. If no route exists
        // --------------------------------------------------

        if (targetBaseUrl == null)
        {
            return Results.NotFound(new
            {
                error = "No route configured for this path",
                path = path.ToString()
            });
        }


        // --------------------------------------------------
        // 4. Remove /api prefix
        //
        // Example:
        //
        // /api/users
        //       ↓
        // /users
        //
        // /api/users/10
        //       ↓
        // /users/10
        // --------------------------------------------------

        var servicePath = path.Value!.Substring("/api".Length);


        // --------------------------------------------------
        // 5. Build target URL
        // --------------------------------------------------

        var targetUrl =
            targetBaseUrl +
            servicePath +
            context.Request.QueryString;


        // --------------------------------------------------
        // 6. Create HttpClient
        // --------------------------------------------------

        var httpClient = httpClientFactory.CreateClient();


        // --------------------------------------------------
        // 7. Create outgoing request
        // --------------------------------------------------

        var proxyRequest = new HttpRequestMessage(
            new HttpMethod(context.Request.Method),
            targetUrl
        );


        // --------------------------------------------------
        // DEBUG
        // --------------------------------------------------

        Console.WriteLine();
        Console.WriteLine("========== GATEWAY REQUEST ==========");
        Console.WriteLine($"Method:       {context.Request.Method}");
        Console.WriteLine($"Incoming:     {context.Request.Path}");
        Console.WriteLine($"Query:        {context.Request.QueryString}");
        Console.WriteLine($"Target:       {targetUrl}");
        Console.WriteLine($"Content-Type: {context.Request.ContentType}");
        Console.WriteLine($"Content-Length: {context.Request.ContentLength}");
        Console.WriteLine("=====================================");
        Console.WriteLine();


        // --------------------------------------------------
        // 8. Forward request body
        // --------------------------------------------------

        // ContentLength can be null when the request uses
        // Transfer-Encoding: chunked.

        if (context.Request.ContentLength > 0 ||
            context.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            proxyRequest.Content =
                new StreamContent(context.Request.Body);

            // Content-Type belongs to the request CONTENT,      
            // not the general request headers.

            if (!string.IsNullOrEmpty(context.Request.ContentType))
            {
                proxyRequest.Content.Headers.ContentType =
                    System.Net.Http.Headers.MediaTypeHeaderValue.Parse(
                        context.Request.ContentType
                    );
            }
        }


        // --------------------------------------------------
        // 9. Hop-by-hop header detection
        // --------------------------------------------------

        static bool IsHopByHopHeader(string headerName)
        {
            return
                headerName.Equals(
                    "Connection",
                    StringComparison.OrdinalIgnoreCase)

                || headerName.Equals(
                    "Keep-Alive",
                    StringComparison.OrdinalIgnoreCase)

                || headerName.Equals(
                    "Proxy-Authenticate",
                    StringComparison.OrdinalIgnoreCase)

                || headerName.Equals(
                    "Proxy-Authorization",
                    StringComparison.OrdinalIgnoreCase)

                || headerName.Equals(
                    "TE",
                    StringComparison.OrdinalIgnoreCase)

                || headerName.Equals(
                    "Trailer",
                    StringComparison.OrdinalIgnoreCase)

                || headerName.Equals(
                    "Transfer-Encoding",
                    StringComparison.OrdinalIgnoreCase)

                || headerName.Equals(
                    "Upgrade",
                    StringComparison.OrdinalIgnoreCase);
        }


        // --------------------------------------------------
        // 10. Forward request headers 
        // --------------------------------------------------

        foreach (var header in context.Request.Headers)
        {
            // Don't forward transport-level headers
            if (IsHopByHopHeader(header.Key))
                continue;


            // Content-Type was already handled above
            if (header.Key.Equals(
                "Content-Type",
                StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }


            proxyRequest.Headers.TryAddWithoutValidation(
                header.Key,
                header.Value.ToArray()
            );
        }


        // --------------------------------------------------
        // 11. Send request to backend service
        // --------------------------------------------------

        HttpResponseMessage proxyResponse;

        try
        {
            proxyResponse = await httpClient.SendAsync(
                proxyRequest,
                HttpCompletionOption.ResponseHeadersRead,
                context.RequestAborted
            );
        }
        catch (HttpRequestException)

        {
            return Results.Problem(
                title: "Backend service unavailable",
                statusCode: StatusCodes.Status502BadGateway
            );
        }
        catch (TaskCanceledException)
        {
            return Results.Problem(
                title: "Backend request timed out or was cancelled",
                statusCode: StatusCodes.Status504GatewayTimeout
            );
        }


        // --------------------------------------------------
        // 12. Copy response headers
        // --------------------------------------------------

        foreach (var header in proxyResponse.Headers)
        {
            if (!IsHopByHopHeader(header.Key))
            {
                context.Response.Headers[header.Key] =
                    header.Value.ToArray();
            }
        }


        foreach (var header in proxyResponse.Content.Headers)
        {
            // Kestrel handles response framing.
            if (header.Key.Equals(
                "Content-Length",
                StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }


            if (IsHopByHopHeader(header.Key))
                continue;


            context.Response.Headers[header.Key] =
                header.Value.ToArray();
        }


        // --------------------------------------------------
        // 13. Copy status code
        // --------------------------------------------------

        context.Response.StatusCode =
            (int)proxyResponse.StatusCode;


        // --------------------------------------------------
        // 14. Stream response body back to client
        // --------------------------------------------------

        await proxyResponse.Content.CopyToAsync(
            context.Response.Body
        );


        return Results.Empty;
    }
);


app.Run();