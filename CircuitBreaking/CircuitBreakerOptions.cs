public class CircuitBreakerOptions
{
    public int FailureThreshold { get; set; } = 3;

    public TimeSpan OpenDuration { get; set; }
        = TimeSpan.FromSeconds(30);
}