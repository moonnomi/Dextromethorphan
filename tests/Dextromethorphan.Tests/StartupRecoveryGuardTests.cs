using Dextromethorphan.App.Diagnostics;
using Dextromethorphan.Infrastructure.Storage;

namespace Dextromethorphan.Tests;

public sealed class StartupRecoveryGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Dextromethorphan.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void RepeatedIncompleteStartupEntersSafeModeAndSuccessResetsIt()
    {
        var paths = new AppPaths(_root);

        Assert.False(new StartupRecoveryGuard(paths).Begin(false));
        Assert.False(new StartupRecoveryGuard(paths).Begin(false));
        Assert.True(new StartupRecoveryGuard(paths).Begin(false));

        new StartupRecoveryGuard(paths).Complete();

        Assert.False(new StartupRecoveryGuard(paths).Begin(false));
        new StartupRecoveryGuard(paths).Complete();
    }

    [Fact]
    public void ExplicitSafeModeDoesNotNeedPriorFailures()
    {
        var guard = new StartupRecoveryGuard(new AppPaths(_root));

        Assert.True(guard.Begin(true));

        guard.Complete();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
