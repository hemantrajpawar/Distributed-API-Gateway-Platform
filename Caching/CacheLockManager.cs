using System.Collections.Concurrent;

public class CacheLockManager
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks
        = new();

    public SemaphoreSlim GetLock(string cacheKey)
    {
        return _locks.GetOrAdd(
            cacheKey,
            _ => new SemaphoreSlim(1, 1) 
        );
    }
}