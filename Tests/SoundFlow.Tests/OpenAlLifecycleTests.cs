using BackendException = SoundFlow.Backends.OpenAL.BackendException;
using Context = SoundFlow.Backends.OpenAL.Devices.Context;
using CoreDeviceConfiguration = SoundFlow.Devices.DeviceConfiguration;
using DeviceConfiguration = SoundFlow.Backends.OpenAL.DeviceConfiguration;
using BackendEngine = SoundFlow.Backends.OpenAL.Engine;
using Functions = SoundFlow.Backends.OpenAL.Interop.Functions;
using IDeviceLifecycle = SoundFlow.Backends.OpenAL.Devices.IDeviceLifecycle;
using PlaybackDevice = SoundFlow.Devices.PlaybackDevice;
using SoundFlow.Devices;
using SoundFlow.Backends.OpenAL;
using SoundFlow.Backends.OpenAL.Devices;
using SoundFlow.Backends.OpenAL.Interop;
using SoundFlow.Features;
using SoundFlow.Playback;
using SoundFlow.Structs;
using Xunit;

namespace SoundFlow.Tests;

public sealed class OpenAlLifecycleTests
{
    [Fact]
    public void FailedContextConstructionClosesTheAlreadyOpenedDevice()
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        using var owner = new Owner(engine);
        var calls = new OfflineLifecycle(engine.Native) { FailContextCreation = true };
        Assert.Throws<BackendException>(() => new Context(engine, owner, null, new DeviceConfiguration(), false, calls));
        Assert.Equal(1, calls.Opened);
        Assert.Equal(1, calls.Closed);
    }

    [Fact]
    public void FailedOutputSwitchRetainsTheContextAndOwnedSources()
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        using var owner = new Owner(engine);
        var calls = new OfflineLifecycle(engine.Native);
        using var context = new SoundFlow.Backends.OpenAL.Devices.Context(engine, owner, null, new SoundFlow.Backends.OpenAL.DeviceConfiguration(), false, calls);
        using var source = context.CreateSource(new SoundFlow.Playback.Clip(new float[4096], TransportTests.Mono).Open(), TransportTests.Mono, false);
        var before = context.GetSnapshot();
        Assert.Throws<BackendException>(() => context.Reopen(new DeviceInfo { Id = "openal:playback:unavailable", Name = "unavailable" }, null));
        var after = context.GetSnapshot();
        Assert.Equal(before.Device, after.Device);
        Assert.Equal(before.Sources, after.Sources);
        Assert.False(source.IsDisposed);
        Assert.Equal(1, calls.ReopenAttempts);
    }

    private sealed class Config : CoreDeviceConfiguration;
    private sealed class Owner(BackendEngine engine) : PlaybackDevice(engine, OpenAlPlaybackTests.Stereo, new Config())
    {
        public override void Start() => IsRunning = true;
        public override void Stop() => IsRunning = false;
        public override void Dispose() { DisposeSources(); MasterMixer.Dispose(); IsDisposed = true; }
    }

    // Fault injection affects only device lifecycle operations. Actual AL rendering/resource semantics still use Soft's offline implementation.
    private sealed unsafe class OfflineLifecycle(Functions native) : IDeviceLifecycle
    {
        public bool FailContextCreation;
        public int Opened;
        public int Closed;
        public int ReopenAttempts;
        public nint Open(bool offline, byte[]? name) { Opened++; return native.alcLoopbackOpenDeviceSOFT(null); }
        public nint CreateContext(nint device, int[] attributes)
        {
            if (FailContextCreation) return 0;
            int[] withFormat = [.. attributes[..^1], 0x1990, 0x1501, 0x1991, 0x1406, 0];
            fixed (int* pointer = withFormat) return native.alcCreateContext(device, pointer);
        }
        public bool Reopen(nint device, byte[]? name, int[] attributes) { ReopenAttempts++; return false; }
        public void DestroyContext(nint context) => native.alcDestroyContext(context);
        public void Close(nint device) { Closed++; native.alcCloseDevice(device); }
    }
}
