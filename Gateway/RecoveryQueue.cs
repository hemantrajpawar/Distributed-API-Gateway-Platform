using System.Collections.Concurrent;

public class RecoveryQueue
{
    private readonly ConcurrentQueue<string> _queue = new();

    private readonly ConcurrentDictionary<string, byte>
        _queuedInstances = new();

    public bool Enqueue(string instance)
    {
        // TryAdd returns false if instance already exists
        if (!_queuedInstances.TryAdd(instance, 0))
        {
            return false;
        }

        _queue.Enqueue(instance);
        return true;
    }

    public bool TryDequeue(out string? instance)
    {
        if (_queue.TryDequeue(out instance))
        {
            _queuedInstances.TryRemove(instance, out _);
            return true;
        }

        instance = null;
        return false;
    }
}