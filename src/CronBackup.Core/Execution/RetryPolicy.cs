namespace CronBackup.Core.Execution;

public sealed record RetryOptions(
    int MaxAttempts = 3,
    TimeSpan? BaseDelay = null,
    TimeSpan? MaxDelay = null,
    double JitterFactor = 0.2)
{
    public TimeSpan EffectiveBaseDelay => BaseDelay ?? TimeSpan.FromSeconds(2);

    public TimeSpan EffectiveMaxDelay => MaxDelay ?? TimeSpan.FromSeconds(30);
}

public sealed class RetryPolicy
{
    private readonly RetryOptions _options;
    private readonly TimeProvider _timeProvider;

    public RetryPolicy(RetryOptions? options = null, TimeProvider? timeProvider = null)
    {
        _options = options ?? new RetryOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> action,
        Func<Exception, bool>? shouldRetry,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        for (var attempt = 1; attempt <= _options.MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await action(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (shouldRetry is null || shouldRetry(exception))
            {
                lastException = exception;
                if (attempt == _options.MaxAttempts)
                {
                    break;
                }

                var delay = CalculateDelay(attempt);
                await Task.Delay(delay, _timeProvider, cancellationToken);
            }
        }

        throw lastException ?? new InvalidOperationException("retry policy exhausted without an exception");
    }

    public Task ExecuteAsync(
        Func<CancellationToken, Task> action,
        Func<Exception, bool>? shouldRetry,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            async token =>
            {
                await action(token);
                return true;
            },
            shouldRetry,
            cancellationToken);

    private TimeSpan CalculateDelay(int attempt)
    {
        var exponential = _options.EffectiveBaseDelay.TotalMilliseconds *
                          Math.Pow(2, attempt - 1);
        var capped = Math.Min(exponential, _options.EffectiveMaxDelay.TotalMilliseconds);
        var jitter = 1 + ((Random.Shared.NextDouble() * 2) - 1) * _options.JitterFactor;
        return TimeSpan.FromMilliseconds(Math.Max(0, capped * jitter));
    }
}
