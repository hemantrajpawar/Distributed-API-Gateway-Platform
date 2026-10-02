using System.Net.Http.Headers;

public static class ProxyRequestBuilder
{
    public static HttpRequestMessage Create(
        HttpContext context,
        string targetUrl)
    {
        // ==============================================
        // 1. Create outgoing request
        // ==============================================

        var proxyRequest =
            new HttpRequestMessage(
                new HttpMethod(
                    context.Request.Method
                ),
                targetUrl
            );


        // ==============================================
        // 2. Forward request body
        // ==============================================

        if (context.Request.ContentLength > 0 ||
            context.Request.Headers.ContainsKey(
                "Transfer-Encoding"))
        {
            proxyRequest.Content =
                new StreamContent(
                    context.Request.Body
                );


            // Content-Type belongs to Content.Headers

            if (!string.IsNullOrEmpty(
                    context.Request.ContentType))
            {
                proxyRequest.Content.Headers.ContentType =
                    MediaTypeHeaderValue.Parse(
                        context.Request.ContentType
                    );
            }
        }


        // ==============================================
        // 3. Forward request headers
        // ==============================================

        foreach (var header in context.Request.Headers)
        {
            // Don't forward hop-by-hop headers
            if (IsHopByHopHeader(header.Key))
            {
                continue;
            }


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


        return proxyRequest;
    }


    // ==============================================
    // Hop-by-hop header detection
    // ==============================================

    public static bool IsHopByHopHeader(
        string headerName)
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
}