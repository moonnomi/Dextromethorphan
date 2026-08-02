using Dextromethorphan.Infrastructure.Storage;

namespace Dextromethorphan.Tests;

public sealed class AppPathsTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void PortableMarkerOrSwitchUsesDataBesideExecutable(bool marker, bool commandSwitch)
    {
        var executable = Path.Combine(Path.GetTempPath(), "portable-app");
        var result = AppPaths.ResolveRoot(
            null,
            executable,
            commandSwitch ? ["app.exe", "--portable"] : ["app.exe"],
            marker,
            Path.Combine(Path.GetTempPath(), "roaming"));

        Assert.True(result.Portable);
        Assert.Equal(Path.Combine(executable, "data"), result.Root);
    }

    [Fact]
    public void ExplicitDataRootOverridesPortableSignals()
    {
        var explicitRoot = Path.Combine(Path.GetTempPath(), "explicit-data");
        var result = AppPaths.ResolveRoot(
            explicitRoot,
            Path.Combine(Path.GetTempPath(), "portable-app"),
            ["app.exe", "--portable"],
            true,
            Path.Combine(Path.GetTempPath(), "roaming"));

        Assert.False(result.Portable);
        Assert.Equal(explicitRoot, result.Root);
    }
}
