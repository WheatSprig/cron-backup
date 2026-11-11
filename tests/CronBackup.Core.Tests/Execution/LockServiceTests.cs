using CronBackup.Core.Execution;
using CronBackup.Core.Tests.TestHelpers;

namespace CronBackup.Core.Tests.Execution;

public sealed class LockServiceTests
{
    [Fact]
    public async Task TryAcquireAsync_ShouldRejectSecondOwner()
    {
        using var temp = new TempDirectory();
        var factory = new FileJobLockFactory(temp.Path);

        await using var first = await factory.TryAcquireAsync("photos", CancellationToken.None);
        await using var second = await factory.TryAcquireAsync("photos", CancellationToken.None);

        Assert.True(first.Acquired);
        Assert.False(second.Acquired);
    }

    [Fact]
    public async Task TryAcquireAsync_ShouldAllowAfterRelease()
    {
        using var temp = new TempDirectory();
        var factory = new FileJobLockFactory(temp.Path);

        var first = await factory.TryAcquireAsync("photos", CancellationToken.None);
        Assert.True(first.Acquired);
        await first.DisposeAsync();

        await using var second = await factory.TryAcquireAsync("photos", CancellationToken.None);
        Assert.True(second.Acquired);
    }
}
