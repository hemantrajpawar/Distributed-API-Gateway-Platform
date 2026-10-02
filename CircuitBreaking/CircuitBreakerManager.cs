using System.Collections.Concurrent;

public class CircuitBreakerManager
{
    private readonly ConcurrentDictionary<
        string,
        CircuitBreaker
    > _circuits = new();

    private readonly CircuitBreakerOptions _options;

    public CircuitBreakerManager(
        CircuitBreakerOptions options)
    {
        _options = options;
    }

    public CircuitBreaker GetCircuit(
        string instance)
    {
        return _circuits.GetOrAdd(
            instance,
            _ => new CircuitBreaker(_options)
        );
    }
}