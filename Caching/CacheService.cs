using StackExchange.Redis;

public class CacheService
{
    private readonly IDatabase _database;

    public CacheService(IDatabase database)
    {
        _database = database;
    }

    public async Task<long> GetVersionAsync(
        string resource)
    {
        var key = $"{resource}:version";

        var value =
            await _database.StringGetAsync(key);

        if (value.HasValue)
        {
            return (long)value;
        }

        await _database.StringSetAsync(
            key,
            1,
            when: When.NotExists
        );

        value =
            await _database.StringGetAsync(key);

        return (long)value;
    }

    public async Task<long> BumpVersionAsync(
        string resource)
    {
        var key = $"{resource}:version";

        return await _database.StringIncrementAsync(key);
    }

    public async Task<string> BuildCacheKeyAsync(
    string resource,
    string method,
    string path)
    {
        var version =
            await GetVersionAsync(resource);

        return $"{resource}:v{version}:{method}:{path}"; 
    }

    public async Task<string?> GetAsync(string key)
    {
        var value =
            await _database.StringGetAsync(key);

        if (!value.HasValue)
        {
            return null;
        }

        return value.ToString();
    }

    public async Task SetAsync(
        string key,
        string response,
        TimeSpan ttl)
    {
        await _database.StringSetAsync(
            key,
            response,
            ttl
        );
    }
}