using Xunit;

namespace SoundFlow.Tests;

public sealed class RuntimePolicyTests
{
    [Fact]
    public void RemovedArchitecturesAreNotDistributedByAnyNativePackage()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "SoundFlow.sln"))) root = root.Parent;
        Assert.NotNull(root);
        foreach (var package in new[] { "Backends/SoundFlow.Backends.MiniAudio", "Backends/SoundFlow.Backends.OpenAL", "Codecs/SoundFlow.Codecs.FFMpeg", "Extensions/SoundFlow.Extensions.WebRtc.Apm" })
            foreach (var rid in new[] { "android-arm", "android-x64", "linux-arm", "win-x86" })
                Assert.False(Directory.Exists(Path.Combine(root.FullName, package, "runtimes", rid)), $"{package} still distributes {rid}");
    }
}
