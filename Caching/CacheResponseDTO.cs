public class CachedResponse
{
    public int StatusCode { get; set; }

    public Dictionary<string, string[]> Headers { get; set; }
        = new();

    public string Body { get; set; } = "";
}