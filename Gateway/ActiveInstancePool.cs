using System.Threading;

public class ActiveInstancePool
{
    private string[] _instances;

    public ActiveInstancePool(string[] instances)
    {
        _instances = instances;
    }

    public string[] GetInstances()
    {
        return Volatile.Read(ref _instances);
    }

    public void Remove(string instance)
    {
        while (true)
        {
            var current = Volatile.Read(ref _instances);

            if (!current.Contains(instance))
            {
                return;
            }

            var updated = current
                .Where(x => x != instance)
                .ToArray();

            if (ReferenceEquals(
                Interlocked.CompareExchange(
                    ref _instances,
                    updated,
                    current
                ),
                current))
            {
                return;
            }
        }
    }

    public void Add(string instance)
    {
        while (true)
        {
            var current = Volatile.Read(ref _instances);

            // Already active
            if (current.Contains(instance))
            {
                return;
            }

            var updated = current
                .Append(instance)
                .ToArray();

            if (ReferenceEquals(
                Interlocked.CompareExchange(
                    ref _instances,
                    updated,
                    current
                ),
                current))
            {
                return;
            }
        }
    }
}