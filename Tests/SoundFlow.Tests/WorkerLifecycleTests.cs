using SoundFlow.Backends.OpenAL.Devices;
using Xunit;

namespace SoundFlow.Tests;

public sealed class WorkerLifecycleTests
{
    [Fact]
    public void CleanupFailuresAreReturnedToTheDisposerInsteadOfEscapingABackgroundThread()
    {
        var worker = new ContextWorker(() => { }, () => { }, () => throw new IOException("Cleanup failed"));
        Assert.Throws<IOException>(worker.Dispose);
    }
    [Fact]
    public void FailedWorkerInitializationRunsItsCleanupBeforeReturningTheFailure()
    {
        var cleaned = false;
        Assert.Throws<IOException>(() => new ContextWorker(() => throw new IOException("Initialization failed"), () => { }, () => cleaned = true));
        Assert.True(cleaned);
    }
}
