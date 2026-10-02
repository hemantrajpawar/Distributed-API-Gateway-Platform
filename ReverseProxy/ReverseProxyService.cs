using System.Net.Http.Headers;

namespace ReverseProxyService;

public class ReverseProxyService
{
    private readonly IHttpClientFactory _httpClientFactory;

    public ReverseProxyService(
        IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<CachedResponse?> ForwardAsync(
        HttpContext context,
        string targetUrl)
    {
        var httpClient =
            _httpClientFactory.CreateClient();

        var proxyRequest =
            ProxyRequestBuilder.Create(
                context,
                targetUrl
            );

        HttpResponseMessage proxyResponse;

        try
        {
            proxyResponse =
                await httpClient.SendAsync(
                    proxyRequest,
                    HttpCompletionOption.ResponseHeadersRead,
                    context.RequestAborted
                );
        }
        catch (HttpRequestException)
        {
            context.Response.StatusCode =
                StatusCodes.Status502BadGateway;

            return null;
        }
        catch (TaskCanceledException)
        {
            context.Response.StatusCode =
                StatusCodes.Status504GatewayTimeout;

            return null;
        }


        // ==================================================
        // Create object that represents the complete response
        // ==================================================

        var cachedResponse = new CachedResponse
        {
            StatusCode = (int)proxyResponse.StatusCode
        };


        // ==================================================
        // Copy response headers
        // ==================================================

        foreach (var header in proxyResponse.Headers)
        {
            if (ProxyRequestBuilder.IsHopByHopHeader(
                    header.Key))
            {
                continue;
            }

            cachedResponse.Headers[header.Key] =
                header.Value.ToArray();

            context.Response.Headers[header.Key] =
                header.Value.ToArray();
        }


        foreach (var header in proxyResponse.Content.Headers)
        {
            if (header.Key.Equals(
                    "Content-Length",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (ProxyRequestBuilder.IsHopByHopHeader(
                    header.Key))
            {
                continue;
            }

            cachedResponse.Headers[header.Key] =
                header.Value.ToArray();

            context.Response.Headers[header.Key] =
                header.Value.ToArray();
        }


        // ==================================================
        // Copy status code
        // ==================================================

        context.Response.StatusCode =
            cachedResponse.StatusCode;


        // ==================================================
        // Read response body
        // ==================================================

        var responseBody =
            await proxyResponse.Content.ReadAsStringAsync(
                context.RequestAborted
            );

        cachedResponse.Body =
            responseBody;


        // ==================================================
        // Send body to client
        // ==================================================

        await context.Response.WriteAsync(
            responseBody,
            context.RequestAborted
        );


        // ==================================================
        // Return complete response information
        // ==================================================

        return cachedResponse;
    }
}