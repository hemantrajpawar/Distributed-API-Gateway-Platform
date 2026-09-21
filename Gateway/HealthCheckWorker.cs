using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;

public class HealthCheckWorker : BackgroundService
{
    private readonly ActiveInstancePool _instancePool;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly RecoveryQueue _recoveryQueue;

    // Number of consecutive failures for each instance
    private readonly ConcurrentDictionary<string, int>
        _failureCounts = new();

    // Instance must fail this many consecutive checks
    // before being removed from the active pool.
    private const int FailureThreshold = 3;

    // Maximum time allowed for one health check.
    private static readonly TimeSpan HealthCheckTimeout =
        TimeSpan.FromSeconds(2);

    // Time between health-check cycles.
    private static readonly TimeSpan CheckInterval =
        TimeSpan.FromSeconds(5);

    public HealthCheckWorker(
        ActiveInstancePool instancePool,
        IHttpClientFactory httpClientFactory,
        RecoveryQueue recoveryQueue)
    {
        _instancePool = instancePool;
        _httpClientFactory = httpClientFactory;
        _recoveryQueue = recoveryQueue;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        var httpClient =
            _httpClientFactory.CreateClient();

        while (!stoppingToken.IsCancellationRequested)
        {
            // ==========================================
            // 1. CHECK ACTIVE INSTANCES
            // ==========================================

            var instances =
                _instancePool.GetInstances();

            var failedInstances =
                new List<string>();

            foreach (var instance in instances)
            {
                var isHealthy =
                    await CheckHealthAsync(
                        httpClient,
                        instance,
                        stoppingToken
                    );

                if (isHealthy)
                {
                    Console.WriteLine(
                        $"{instance} → Healthy"
                    );

                    // Reset consecutive failure count
                    _failureCounts.TryRemove(
                        instance,
                        out _
                    );
                }
                else
                {
                    var failures =
                        _failureCounts.AddOrUpdate(
                            instance,
                            1,
                            (_, count) => count + 1
                        );

                    Console.WriteLine(
                        $"{instance} → Failed " +
                        $"({failures}/{FailureThreshold})"
                    );

                    if (failures >= FailureThreshold)
                    {
                        failedInstances.Add(instance);
                    }
                }
            }

            // ==========================================
            // 2. REMOVE FAILED INSTANCES
            // ==========================================

            foreach (var instance in failedInstances)
            {
                _instancePool.Remove(instance);

                _recoveryQueue.Enqueue(instance);

                _failureCounts.TryRemove(
                    instance,
                    out _
                );

                Console.WriteLine(
                    $"{instance} → Removed from active pool"
                );
            }

            // ==========================================
            // 3. CHECK RECOVERY QUEUE
            // ==========================================

            var stillUnhealthy =
                new List<string>();

            while (_recoveryQueue.TryDequeue(
                out var instance))
            {
                if (instance == null)
                {
                    continue;
                }

                var isHealthy =
                    await CheckHealthAsync(
                        httpClient,
                        instance,
                        stoppingToken
                    );

                if (isHealthy)
                {
                    Console.WriteLine(
                        $"{instance} → Recovered"
                    );

                    _instancePool.Add(instance);
                }
                else
                {
                    Console.WriteLine(
                        $"{instance} → Still unhealthy"
                    );

                    stillUnhealthy.Add(instance);
                }
            }

            // Put failed recovery instances
            // back into the queue.
            foreach (var instance in stillUnhealthy)
            {
                _recoveryQueue.Enqueue(instance);
            }

            // ==========================================
            // 4. WAIT BEFORE NEXT CYCLE
            // ==========================================

            try
            {
                await Task.Delay(
                    CheckInterval,
                    stoppingToken
                );
            }
            catch (OperationCanceledException)
            {
                // Application is shutting down.
                break;
            }
        }
    }

    private async Task<bool> CheckHealthAsync(
        HttpClient httpClient,
        string instance,
        CancellationToken stoppingToken)
    {
        using var timeoutCts =
            CancellationTokenSource
                .CreateLinkedTokenSource(
                    stoppingToken
                );

        timeoutCts.CancelAfter(
            HealthCheckTimeout 
        );

        try
        {
            var response =
                await httpClient.GetAsync(
                    $"{instance}/health",
                    timeoutCts.Token
                );

            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}