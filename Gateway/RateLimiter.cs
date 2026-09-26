using StackExchange.Redis;

public class RateLimiter
{
    private readonly IDatabase _database;

    private const int MaxRequests = 4;

    private static readonly TimeSpan Window =
        TimeSpan.FromMinutes(1);

    private const string RateLimitScript = """
        local count = redis.call('INCR', KEYS[1])

        if count == 1 then
            redis.call('EXPIRE', KEYS[1], ARGV[1])
        end

        local limit = tonumber(ARGV[2])

        if count > limit then
            return {count, 0, 0}
        end

        return {count, limit - count, 1}
        """;

    public RateLimiter(IDatabase database)
    {
        _database = database;
    }

    public async Task<RateLimitResult> CheckAsync(
        string ip)
    {
        var key =
            $"rate_limit:ip:{ip}";

        var ttlSeconds =
            (int)Window.TotalSeconds;

        var result =
            (RedisResult[])await _database.ScriptEvaluateAsync(
                RateLimitScript,
                new RedisKey[]
                {
                    key
                },
                new RedisValue[]
                { 
                    ttlSeconds,
                    MaxRequests
                }
            );

        var count =
            (int)result[0];

        var remaining =
            (int)result[1];

        var allowed =
            (int)result[2] == 1;

        return new RateLimitResult(
            allowed,
            count,
            remaining
        );
    }
}

public record RateLimitResult(
    bool Allowed,
    int Count,
    int Remaining
);