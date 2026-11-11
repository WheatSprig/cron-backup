using CronBackup.Core.Execution;
using CronBackup.Core.Tests.TestHelpers;

namespace CronBackup.Core.Tests.Execution;

public sealed class RetryPolicyTests
{
    private static RetryPolicy CreatePolicy() =>
        new(new RetryOptions(3, TimeSpan.Zero, TimeSpan.Zero, 0), new FixedTimeProvider(DateTimeOffset.UtcNow));

    [Fact]
    public async Task ExecuteAsync_ShouldRetryUntilSuccess()
    {
        var attempts = 0;
        var policy = CreatePolicy();

        var result = await policy.ExecuteAsync(
            _ =>
            {
                attempts++;
                if (attempts < 3)
                {
                    throw new IOException("temporary");
                }

                return Task.FromResult(42);
            },
            _ => true,
            CancellationToken.None);

        Assert.Equal(42, result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldStopWhenPredicateRejects()
    {
        var attempts = 0;
        var policy = CreatePolicy();

        await Assert.ThrowsAsync<InvalidOperationException>(() => policy.ExecuteAsync(
            _ =>
            {
                attempts++;
                throw new InvalidOperationException("permanent");
            },
            _ => false,
            CancellationToken.None));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRethrowAfterMaxAttempts()
    {
        var attempts = 0;
        var policy = CreatePolicy();

        await Assert.ThrowsAsync<IOException>(() => policy.ExecuteAsync(
            _ =>
            {
                attempts++;
                throw new IOException("still failing");
            },
            _ => true,
            CancellationToken.None));

        Assert.Equal(3, attempts);
    }
}
