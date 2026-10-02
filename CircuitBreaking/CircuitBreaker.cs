public class CircuitBreaker
{
    private readonly object _lock = new();

    private CircuitState _state =
        CircuitState.Closed;

    private int _failureCount = 0;

    private DateTime _openedAt;

    private readonly CircuitBreakerOptions _options;

    public CircuitBreaker(
        CircuitBreakerOptions options)
    {
        _options = options;
    }

    public bool AllowRequest()
    {
        lock (_lock)
        {
            if (_state == CircuitState.Closed)
            {
                return true;
            }

            if (_state == CircuitState.Open)
            {
                return CheckOpenState();
            }

            return false;
        }
    }

    private bool CheckOpenState()
    {
        var elapsed =
            DateTime.UtcNow - _openedAt;

        if (elapsed < _options.OpenDuration)
        {
            return false;
        }

        _state = CircuitState.HalfOpen;

        return true;
    }

    public void RecordSuccess()
    {
        lock (_lock)
        {
            _failureCount = 0;

            _state = CircuitState.Closed;
        }
    }

    public void RecordFailure()
    {
        lock (_lock)
        {
            if (_state == CircuitState.HalfOpen)
            {
                _state = CircuitState.Open;

                _openedAt = DateTime.UtcNow;

                return;
            }

            _failureCount++;

            if (_failureCount >=
                _options.FailureThreshold)
            {
                _state = CircuitState.Open;

                _openedAt = DateTime.UtcNow;
            }
        }
    }
}