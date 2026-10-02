using System.Threading;

public record SelectedInstance(
    string BaseUrl,
    CircuitBreaker Circuit
);

public class InstanceSelector
{
    private readonly ActiveInstancePool _instancePool;
    private readonly CircuitBreakerManager _circuitBreakerManager;

    private static int _instanceIndex = 0;

    public InstanceSelector(
        ActiveInstancePool instancePool,
        CircuitBreakerManager circuitBreakerManager)
    {
        _instancePool = instancePool;
        _circuitBreakerManager = circuitBreakerManager;
    }

    public SelectedInstance? Select()
    {
        var instances =
            _instancePool.GetInstances();

        if (instances.Length == 0)
        {
            return null;
        }

        for (
            int attempt = 0;
            attempt < instances.Length;
            attempt++)
        {
            var index =
                Interlocked.Increment(
                    ref _instanceIndex
                )
                % instances.Length;

            var candidate =
                instances[index];

            var circuit =
                _circuitBreakerManager.GetCircuit(
                    candidate
                );

            if (circuit.AllowRequest())
            {
                return new SelectedInstance(
                    candidate,
                    circuit
                );
            }
        }

        return null;
    }
}