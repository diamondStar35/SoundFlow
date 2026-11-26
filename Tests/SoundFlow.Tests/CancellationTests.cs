using SoundFlow.Playback;
using Xunit;

namespace SoundFlow.Tests;

public sealed class CancellationTests
{
    [Fact]
    public async Task ManagedIoBridgeCancelsAReadWithoutThrowingAcrossTheNativeCallback()
    {
        using var stream = new WaitingStream();
        using var input = new StreamInput(stream);
        using var cancel = new CancellationTokenSource();
        input.Begin(cancel.Token);
        var read = Task.Run(() => input.Read(new byte[128]));
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancel.Cancel();
        Assert.Equal(0, await read.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.ThrowsAny<OperationCanceledException>(() => input.ThrowIfFailed());
    }

    [Fact]
    public void IoFailureIsRethrownAsAnErrorRatherThanReportedAsNaturalCompletion()
    {
        using var input = new StreamInput(new FailingStream());
        input.Begin();
        Assert.Equal(0, input.Read(new byte[16]));
        Assert.Throws<IOException>(() => input.ThrowIfFailed());
    }

    private sealed class FailingStream : MemoryStream
    {
        public override int Read(Span<byte> buffer) => throw new IOException("read failure");
    }
    private sealed class WaitingStream : MemoryStream
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
