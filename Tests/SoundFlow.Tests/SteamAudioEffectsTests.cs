using System.Numerics;
using SoundFlow.Playback;
using SoundFlow.Processing;
using SoundFlow.Spatial;
using SoundFlow.SteamAudio;
using SoundFlow.Structs;
using Xunit;
using Native = global::SteamAudio;

namespace SoundFlow.Tests;

public sealed class SteamAudioEffectsTests
{
    [Fact]
    public void DistanceCaptureCanExcludeDryAirAbsorption()
    {
        using var renderer = new Renderer();
        var state = new Spatialization(new()
        {
            Distance = new() { Model = AttenuationModel.None },
            AirAbsorption = new Bands(.1f, .1f, .1f)
        });
        using var distance = renderer.CreateDirectProcessor(Mono, state, DirectEffects.Distance);
        var input = Signal(1);
        var output = new float[input.Length];
        distance.Process(input, output, 256);
        for (var i = 0; i < input.Length; i++)
            Assert.Equal(input[i], output[i], 6);
    }

    [Fact]
    public void SpeakerDevicesRejectAmbisonicsOutputMetadata()
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        var field = Stereo with { Channels = 4, Layout = ChannelLayout.Ambisonics, Ambisonics = new(1) };
        Assert.Throws<NotSupportedException>(() => engine.CreateRenderDevice(field));
        using var softwareEngine = new HeadlessEngine();
        Assert.Throws<NotSupportedException>(() => new SoftwareDevice(softwareEngine, field));
    }

    [Fact]
    public void CapturedAmbisonicsCannotBeSentToNativeSpeakerEffects()
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        using var device = engine.CreateRenderDevice(Stereo);
        using var renderer = new Renderer();
        var builder = new Builder().Add(format => renderer.CreateAmbisonicsEncoder(format, new(), 1));
        var field = builder.Capture("field");
        builder.Add(format => renderer.CreateAmbisonicsBinaural(format));
        using var source = device.CreateSource(new Clip(Signal(1), Mono), new SourceOptions { RenderProcessing = builder.Build() });
        Assert.True(device.TryGetFeature<SoundFlow.Effects.IEffects>(out var effects));
        using var room = effects.CreateSlot(new SoundFlow.Effects.EaxReverbSettings());
        Assert.True(source.TryGetFeature<SoundFlow.Features.IProcessingSends>(out var sends));
        Assert.Throws<NotSupportedException>(() => sends.SetSend(0, field, room));
    }

    [Fact]
    public void NativeRenderingCannotRunOnDecodeWorkers()
    {
        using var scheduler = new DecodeScheduler(new());
        using var renderer = new Renderer();
        using var transport = new Transport(Mono, new Clip(Signal(1), Mono).Open());
        var chain = new Builder().Add(format => renderer.CreateBinauralProcessor(format, new())).Build();
        using var runner = chain.CreateRunner(Mono, 256);
        Assert.Throws<ArgumentException>(() => new DecodeSession(scheduler, transport, frames: 256, processing: runner));
    }

    [Fact]
    public void SoftwareDeviceTransferPreservesNativeWorkspaceOwnership()
    {
        using var engine = new HeadlessEngine();
        using var renderer = new Renderer();
        using var first = new SoftwareDevice(engine);
        using var second = new SoftwareDevice(engine);
        using var source = renderer.CreateSource(first, new Clip(Signal(1), Mono));
        Assert.Throws<NotSupportedException>(() => first.TransferSourcesTo(second));
        Assert.Same(first, source.Device);
        source.Play();
        first.MasterMixer.PrepareForRender();
        var output = new float[512];
        first.MasterMixer.Process(output, 2);
        Assert.Contains(output, sample => Math.Abs(sample) > .001f);
    }

    private sealed class SoftwareConfiguration : SoundFlow.Devices.DeviceConfiguration;
    private sealed class SoftwareDevice(HeadlessEngine engine, AudioFormat? format = null)
        : SoundFlow.Devices.PlaybackDevice(engine, format ?? OpenAlPlaybackTests.Stereo, new SoftwareConfiguration())
    {
        public override void Start() => IsRunning = true;
        public override void Stop() => IsRunning = false;
        public override void Dispose()
        {
            DisposeSources();
            MasterMixer.Dispose();
            IsDisposed = true;
        }
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(3, 0)]
    [InlineData(1, 1)]
    [InlineData(3, 1)]
    [InlineData(1, 2)]
    [InlineData(3, 2)]
    [InlineData(1, 3)]
    [InlineData(3, 3)]
    [InlineData(1, 4)]
    [InlineData(3, 4)]
    public void AmbisonicsRotationAndDecodersMatchNativeIncludingTails(int order, int mode)
    {
        using var renderer = new Renderer();
        renderer.Listener = new()
        {
            Forward = Vector3.UnitX
        };
        var format = Mono with
        {
            Channels = (order + 1) * (order + 1),
            Layout = ChannelLayout.Ambisonics,
            Ambisonics = new(order)
        };
        using var processor = mode switch
        {
            0 => (SoundFlow.SteamAudio.Processor)renderer.CreateAmbisonicsRotation(format),
            1 => renderer.CreateAmbisonicsPanning(format, SpeakerLayout.Quad),
            2 => renderer.CreateAmbisonicsBinaural(format),
            _ => renderer.CreateAmbisonicsDecoder(format, mode == 4, mode == 4 ? SpeakerLayout.Stereo : SpeakerLayout.Quad)
        };
        using var context = new Native.Context();
        using var hrtf = new Native.Hrtf(context, new(48000, 256));
        var handle = new Native.Interop.HrtfHandle(hrtf.DangerousGetHandle());
        var layout = new Native.Interop.SpeakerLayout { Type = mode == 4 ? Native.Interop.SpeakerLayoutType.Stereo : Native.Interop.SpeakerLayoutType.Quadraphonic };
        using var rotation = new Native.Effects.AmbisonicsRotation(context, new(48000, 256), new()
        {
            MaxOrder = order
        });
        using var panning = new Native.Effects.AmbisonicsPanning(context, new(48000, 256), new()
        {
            MaxOrder = order,
            SpeakerLayout = layout
        });
        using var binaural = new Native.Effects.AmbisonicsBinaural(context, new(48000, 256), new()
        {
            MaxOrder = order,
            Hrtf = handle
        }, hrtf);
        using var decode = new Native.Effects.AmbisonicsDecode(context, new(48000, 256), new()
        {
            MaxOrder = order,
            SpeakerLayout = layout,
            Hrtf = handle
        }, hrtf);
        using var input = new Native.AudioBuffer(context, format.Channels, 256);
        using var output = new Native.AudioBuffer(context, processor.OutputFormat.Channels, 256);
        var orientation = new Native.Interop.CoordinateSpace3 { Ahead = new() { X = 1 }, Right = new() { Z = 1 }, Up = new() { Y = 1 } };
        var source = Signal(format.Channels);
        var actual = new float[256 * processor.OutputFormat.Channels];
        var expected = new float[actual.Length];
        input.Deinterleave(source);
        var nativeState = mode switch
        {
            0 => rotation.Process(new() { Order = order, Orientation = orientation }, input, output),
            1 => panning.Process(new() { Order = order }, input, output),
            2 => binaural.Process(new() { Order = order, Hrtf = handle }, input, output),
            _ => decode.Process(new() { Order = order, Hrtf = handle, Orientation = orientation, Binaural = mode == 4 ? Native.Interop.Bool.True : Native.Interop.Bool.False }, input, output)
        };
        output.Interleave(expected);
        processor.Process(source, actual, 256);
        Equal(expected, actual);
        var drained = 0;
        while (nativeState == Native.Interop.AudioEffectState.TailRemaining)
        {
            nativeState = mode switch
            {
                0 => rotation.GetTail(output),
                1 => panning.GetTail(output),
                2 => binaural.GetTail(output),
                _ => decode.GetTail(output)
            };
            output.Interleave(expected);
            Assert.Equal(256, processor.Drain(actual));
            for (var i = 0; i < actual.Length; i++)
                Assert.InRange(MathF.Abs(expected[i] - actual[i]), 0, 2e-6f);
            drained += 256;
            Assert.InRange(drained, 0, processor.MaximumTailFrames);
        }
        Assert.Equal(0, processor.Drain(actual));
    }

    [Fact]
    public void AirAbsorptionAttenuatesHighFrequenciesMoreThanLowFrequencies()
    {
        using var renderer = new Renderer();
        double Gain(float frequency)
        {
            var state = new Spatialization(new()
            {
                Position = new(100, 0, 0),
                Distance = new()
                {
                    Model = AttenuationModel.None
                },
                AirAbsorptionModel = new()
            });
            using var processor = renderer.CreateDirectProcessor(Mono, state);
            var input = new float[256];
            var output = new float[256];
            double inputEnergy = 0, outputEnergy = 0;
            for (var block = 0; block < 20; block++)
            {
                for (var i = 0; i < input.Length; i++)
                    input[i] = .2f * MathF.Sin(2 * MathF.PI * frequency * (block * 256 + i) / 48000);
                processor.Process(input, output, 256);
                if (block < 10)
                    continue;
                for (var i = 0; i < input.Length; i++)
                {
                    inputEnergy += input[i] * input[i];
                    outputEnergy += output[i] * output[i];
                }
            }
            return Math.Sqrt(outputEnergy / inputEnergy);
        }
        var low = Gain(100);
        var high = Gain(12000);
        Assert.InRange(low, .8, 1.01);
        Assert.InRange(high, 0, low * .5);
    }

    [Fact]
    public void SpeakerBlendZeroDuplicatesMonoWithoutPositionalGain()
    {
        using var renderer = new Renderer();
        var state = new Spatialization(new()
        {
            Position = Vector3.UnitX,
            SpatialBlend = 0
        });
        using var panning = renderer.CreatePanningProcessor(Mono, state);
        var input = Signal(1);
        var output = new float[512];
        panning.Process(input, output, 256);
        for (var i = 0; i < input.Length; i++)
        {
            Assert.Equal(input[i], output[i * 2]);
            Assert.Equal(input[i], output[i * 2 + 1]);
        }
    }

    [Fact]
    public void SurroundHelperRejectsLiveStereoControls()
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        using var device = engine.CreateRenderDevice(Stereo with
        {
            Channels = 4,
            Layout = ChannelLayout.Quad
        });
        using var renderer = new Renderer(new()
        {
            Mode = RenderingMode.Speakers,
            Speakers = SpeakerLayout.Quad
        });
        using var source = renderer.CreateSource(device, new Clip(Signal(1), Mono));
        Assert.True(source.TryGetFeature<ISpatialization>(out var state));
        Assert.Throws<ArgumentException>(() => state.Settings = state.Settings with { Pan = 0 });
        Assert.Throws<ArgumentException>(() => state.Settings = state.Settings with { Width = 2 });
        Assert.Equal(.5f, state.Settings.Pan);
    }

    [Fact]
    public async Task ConcurrentConstructionDoesNotChangeLiveHrtfResults()
    {
        using var renderer = new Renderer();
        using var reference = new Renderer();
        using var live = renderer.CreateBinauralProcessor(Mono, new(new()
        {
            Position = Vector3.UnitX
        }));
        using var expectedEffect = reference.CreateBinauralProcessor(Mono, new(new()
        {
            Position = Vector3.UnitX
        }));
        var input = Signal(1);
        var actual = new float[512];
        var expected = new float[512];
        using var ready = new ManualResetEventSlim();
        using var start = new ManualResetEventSlim();
        var construction = Task.Run(() =>
        {
            ready.Set();
            start.Wait();
            for (var i = 0; i < 24; i++)
            {
                using var binaural = renderer.CreateBinauralProcessor(Mono, new());
                using var surround = renderer.CreateVirtualSurroundProcessor(Stereo, SpeakerLayout.Stereo);
                using var decode = renderer.CreateAmbisonicsDecoder(Mono with
                {
                    Channels = 4,
                    Layout = ChannelLayout.Ambisonics,
                    Ambisonics = new(1)
                });
            }
        });
        Assert.True(ready.Wait(3000));
        start.Set();
        for (var block = 0; block < 200; block++)
        {
            live.Process(input, actual, 256);
            expectedEffect.Process(input, expected, 256);
            Equal(expected, actual);
        }
        await construction;
    }

    [Fact]
    public void ExtendedEffectsAllocateNoManagedMemoryWhileRendering()
    {
        using var renderer = new Renderer();
        var state = new Spatialization(new()
        {
            Position = Vector3.UnitX,
            AirAbsorptionModel = new(),
            DirectivityModel = new()
        });
        var builder = new Builder().Add(format => renderer.CreateDirectProcessor(format, state));
        builder.Add(format => renderer.CreateAmbisonicsEncoder(format, state, 3));
        builder.Add(format => renderer.CreateAmbisonicsConverter(format, AmbisonicsConvention.SN3D));
        builder.Add(format => renderer.CreateAmbisonicsConverter(format, AmbisonicsConvention.N3D));
        builder.Add(format => renderer.CreateAmbisonicsRotation(format));
        builder.Add(format => renderer.CreateAmbisonicsBinaural(format));
        builder.Add(format => renderer.CreateStereoProcessor(format, state));
        using var runner = builder.Build().CreateRunner(Mono, 256);
        var input = Signal(1);
        var output = new float[512];
        var captures = runner.CreateCaptures();
        runner.Process(input, output, 256, captures);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 32; i++)
            runner.Process(input, output, 256, captures);
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    [Theory]
    [InlineData(false, false, 1f, 1f)]
    [InlineData(true, false, .5f, 2f)]
    [InlineData(true, true, 1f, 1f)]
    [InlineData(false, false, 2f, 0f)]
    public unsafe void AirAbsorptionModelsAndManualOverridesMatchNative(bool custom, bool manual, float metres, float strength)
    {
        using var renderer = new Renderer(new()
        {
            MetresPerUnit = metres
        });
        var state = new Spatialization(new()
        {
            Position = Vector3.UnitX * 10,
            Distance = new()
            {
                Model = AttenuationModel.None
            },
            AirAbsorptionModel = new()
            {
                Strength = strength,
                Coefficients = custom ? new AbsorptionCoefficients(.01f, .05f, .1f) : null
            },
            AirAbsorption = manual ? new Bands(.7f, .6f, .5f) : null
        });
        using var effect = renderer.CreateDirectProcessor(Mono, state);
        using var context = new Native.Context();
        using var native = new Native.Effects.Direct(context, new(48000, 256), new()
        {
            NumChannels = 1
        });
        using var input = new Native.AudioBuffer(context, 1, 256);
        using var output = new Native.AudioBuffer(context, 1, 256);
        var model = new Native.Interop.AirAbsorptionModel();
        if (custom)
        {
            model.Type = Native.Interop.AirAbsorptionModelType.Exponential;
            model.Coefficients[0] = .01f;
            model.Coefficients[1] = .05f;
            model.Coefficients[2] = .1f;
        }
        var parameters = new Native.Interop.DirectEffectParams { Flags = Native.Interop.DirectEffectFlags.ApplyDistanceAttenuation | Native.Interop.DirectEffectFlags.ApplyAirAbsorption, DistanceAttenuation = 1 };
        Native.Interop.Api.AirAbsorptionCalculate(new(context.DangerousGetHandle()), new()
        {
            X = 10 * metres * strength
        }, default, &model, parameters.AirAbsorption);
        if (manual)
        {
            parameters.AirAbsorption[0] = .7f;
            parameters.AirAbsorption[1] = .6f;
            parameters.AirAbsorption[2] = .5f;
        }
        var source = Signal(1);
        var expected = new float[256];
        var actual = new float[256];
        input.Deinterleave(source);
        native.Process(parameters, input, output);
        output.Interleave(expected);
        effect.Process(source, actual, 256);
        Equal(expected, actual);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public unsafe void DipoleOrientationMatchesNativeAndRejectsRearSound(bool rear)
    {
        using var renderer = new Renderer();
        var listener = rear ? Vector3.UnitZ : -Vector3.UnitZ;
        renderer.Listener = new()
        {
            Position = listener
        };
        var state = new Spatialization(new()
        {
            Distance = new()
            {
                Model = AttenuationModel.None
            },
            DirectivityModel = new()
        });
        using var effect = renderer.CreateDirectProcessor(Mono, state);
        var model = new Native.Interop.Directivity { DipoleWeight = .5f, DipolePower = 1 };
        var coordinates = new Native.Interop.CoordinateSpace3 { Ahead = new() { Z = -1 }, Up = new() { Y = 1 }, Right = new() { X = 1 } };
        using var context = new Native.Context();
        var gain = Native.Interop.Api.DirectivityCalculate(new(context.DangerousGetHandle()), coordinates, new()
        {
            Z = listener.Z
        }, &model);
        using var native = new Native.Effects.Direct(context, new(48000, 256), new()
        {
            NumChannels = 1
        });
        using var input = new Native.AudioBuffer(context, 1, 256);
        using var output = new Native.AudioBuffer(context, 1, 256);
        var source = Signal(1);
        var expected = new float[256];
        var actual = new float[256];
        input.Deinterleave(source);
        native.Process(new()
        {
            Flags = Native.Interop.DirectEffectFlags.ApplyDistanceAttenuation | Native.Interop.DirectEffectFlags.ApplyDirectivity,
            DistanceAttenuation = 1,
            Directivity = gain
        }, input, output);
        output.Interleave(expected);
        effect.Process(source, actual, 256);
        for (var i = 0; i < actual.Length; i++)
            Assert.InRange(MathF.Abs(expected[i] - actual[i]), 0, 2e-6f);
        Assert.Equal(rear ? 0 : 1, gain, 5);
    }

    [Fact]
    public void AllStagesUseOneSnapshotEvenIfControlsChangeBetweenEffects()
    {
        using var renderer = new Renderer();
        var state = new Spatialization(new()
        {
            SpatialBlend = 0,
            Distance = new()
            {
                Model = AttenuationModel.None
            }
        });
        var builder = new Builder().Add(format => renderer.CreateDirectProcessor(format, state));
        builder.Add(format => new UpdateControls(format, () => state.Settings = state.Settings with { Pan = 0 }));
        builder.Add(format => renderer.CreateBinauralProcessor(format, state));
        builder.Add(format => renderer.CreateStereoProcessor(format, state));
        using var runner = builder.Build().CreateRunner(Stereo, 256);
        var source = Signal(2);
        var output = new float[512];
        var captures = runner.CreateCaptures();
        runner.Process(source, output, 256, captures);
        Assert.Equal(0, state.AppliedGeneration);
        Assert.Contains(output.Where((_, i) => i % 2 == 1), sample => Math.Abs(sample) > .001f);
        runner.Process(source, output, 256, captures);
        Assert.Equal(1, state.AppliedGeneration);
        Assert.All(output.Where((_, i) => i % 2 == 1), sample => Assert.Equal(0, sample));
    }

    [Fact]
    public void SharedNativeWorkspacesRejectTwoOutputDomains()
    {
        using var renderer = new Renderer();
        var first = new Builder().Add(format => renderer.CreateBinauralProcessor(format, new())).Build();
        var second = new Builder().Add(format => renderer.CreateBinauralProcessor(format, new())).Build();
        using var a = first.CreateRunner(Mono, 256);
        using var b = second.CreateRunner(Mono, 256);
        a.BindRenderDomain(new object());
        Assert.Throws<InvalidOperationException>(() => b.BindRenderDomain(new object()));
    }

    [Fact]
    public void ConcurrentSnapshotPublishersKeepGenerationNumbersMonotonic()
    {
        using var renderer = new Renderer();
        var state = new Spatialization();
        Parallel.For(0, 64, i => { state.Settings = new() { Position = new(i, 0, -1) }; renderer.Listener = new() { Position = new(0, i, 0) }; });
        using var effect = renderer.CreateBinauralProcessor(Mono, state);
        effect.Process(Signal(1), new float[512], 256);
        Assert.Equal(64, state.AppliedGeneration);
        Assert.Equal(64, state.AppliedListenerGeneration);
    }

    private sealed class UpdateControls(AudioFormat format, Action update) : SoundFlow.Processing.Processor(format, format)
    {
        public override void Process(ReadOnlySpan<float> input, Span<float> output, int frames)
        {
            update();
            input.CopyTo(output);
        }
    }

    private static readonly AudioFormat Stereo = OpenAlPlaybackTests.Stereo;
    private static readonly AudioFormat Mono = TransportTests.Mono;
    private static float[] Signal(int channels) => Enumerable.Range(0, 256 * channels).Select(i => .2f * MathF.Sin(i * .13f)).ToArray();
    private static void Equal(float[] expected, float[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        Assert.Contains(actual, sample => MathF.Abs(sample) > 1e-5f);
        for (var i = 0; i < expected.Length; i++)
            Assert.InRange(MathF.Abs(expected[i] - actual[i]), 0, 2e-6f);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ZeroBlendPreservesOrdinaryAudioExactly(int channels)
    {
        using var renderer = new Renderer();
        var inputFormat = channels == 1 ? Mono : Stereo;
        var state = new Spatialization(new()
        {
            Position = Vector3.UnitX,
            SpatialBlend = 0
        });
        using var effect = renderer.CreateBinauralProcessor(inputFormat, state);
        var source = Signal(channels);
        var output = new float[512];
        effect.Process(source, output, 256);
        for (var i = 0; i < 256; i++)
        {
            Assert.Equal(source[i * channels], output[i * 2]);
            Assert.Equal(source[i * channels + (channels == 1 ? 0 : 1)], output[i * 2 + 1]);
        }
    }

    [Theory]
    [InlineData(.25f, HrtfInterpolation.Nearest)]
    [InlineData(.5f, HrtfInterpolation.Bilinear)]
    [InlineData(1f, HrtfInterpolation.Nearest)]
    public void FractionalBlendAndInterpolationMatchTheNativeWrapper(float blend, HrtfInterpolation interpolation)
    {
        using var renderer = new Renderer();
        var state = new Spatialization(new()
        {
            Position = Vector3.UnitX,
            SpatialBlend = blend,
            Interpolation = interpolation
        });
        using var effect = renderer.CreateBinauralProcessor(Stereo, state);
        using var context = new Native.Context();
        using var hrtf = new Native.Hrtf(context, new(48000, 256));
        using var native = new Native.Effects.Binaural(context, hrtf);
        using var input = new Native.AudioBuffer(context, 2, 256);
        using var output = new Native.AudioBuffer(context, 2, 256);
        var source = Signal(2);
        var expected = new float[512];
        var actual = new float[512];
        input.Deinterleave(source);
        native.Process(Vector3.UnitX, input, output, blend, interpolation == HrtfInterpolation.Nearest
            ? Native.Interop.HrtfInterpolation.Nearest : Native.Interop.HrtfInterpolation.Bilinear);
        output.Interleave(expected);
        effect.Process(source, actual, 256);
        Equal(expected, actual);
    }

    [Fact]
    public void PanningMatchesNativeAndCustomDirectionsAreCopied()
    {
        using var renderer = new Renderer();
        var state = new Spatialization(new()
        {
            Position = Vector3.UnitX
        });
        using var effect = renderer.CreatePanningProcessor(Mono, state);
        using var context = new Native.Context();
        using var native = new Native.Effects.Panning(context, new(48000, 256), new()
        {
            SpeakerLayout = new()
            {
                Type = Native.Interop.SpeakerLayoutType.Stereo
            }
        });
        using var input = new Native.AudioBuffer(context, 1, 256);
        using var output = new Native.AudioBuffer(context, 2, 256);
        var source = Signal(1);
        var expected = new float[512];
        var actual = new float[512];
        input.Deinterleave(source);
        native.Process(new()
        {
            Direction = new()
            {
                X = 1
            }
        }, input, output);
        output.Interleave(expected);
        effect.Process(source, actual, 256);
        Equal(expected, actual);
        Assert.True(actual.Where((_, i) => i % 2 == 1).Sum(x => x * x) > actual.Where((_, i) => i % 2 == 0).Sum(x => x * x));
        var directions = new[] { -Vector3.UnitX, Vector3.UnitX };
        var layout = SpeakerLayout.Custom(directions);
        directions[0] = Vector3.UnitZ;
        Assert.Equal(-Vector3.UnitX, layout.Directions[0]);
        using var custom = renderer.CreatePanningProcessor(Mono, state, layout);
        custom.Process(source, actual, 256);
        Assert.All(actual, sample => Assert.True(float.IsFinite(sample)));
    }

    [Fact]
    public void VirtualSurroundMatchesNativeForQuadInput()
    {
        using var renderer = new Renderer();
        var quad = Mono with
        {
            Channels = 4,
            Layout = ChannelLayout.Quad
        };
        using var effect = renderer.CreateVirtualSurroundProcessor(quad, SpeakerLayout.Quad);
        using var context = new Native.Context();
        using var hrtf = new Native.Hrtf(context, new(48000, 256));
        using var native = new Native.Effects.VirtualSurround(context, new(48000, 256),
            new()
            {
                SpeakerLayout = new()
                {
                    Type = Native.Interop.SpeakerLayoutType.Quadraphonic
                },
                Hrtf = new(hrtf.DangerousGetHandle())
            }, hrtf);
        using var input = new Native.AudioBuffer(context, 4, 256);
        using var output = new Native.AudioBuffer(context, 2, 256);
        var source = Signal(4);
        var expected = new float[512];
        var actual = new float[512];
        input.Deinterleave(source);
        native.Process(new()
        {
            Hrtf = new(hrtf.DangerousGetHandle())
        }, input, output);
        output.Interleave(expected);
        effect.Process(source, actual, 256);
        Equal(expected, actual);
    }

    [Theory]
    [InlineData(.5f, 1f)]
    [InlineData(0f, 1f)]
    [InlineData(1f, 1f)]
    [InlineData(.5f, 0f)]
    [InlineData(.5f, 2f)]
    public void StereoBalanceAndWidthHaveDefinedEndpoints(float pan, float width)
    {
        using var renderer = new Renderer();
        var state = new Spatialization(new()
        {
            Pan = pan,
            Width = width
        });
        using var effect = renderer.CreateStereoProcessor(Stereo, state);
        var input = Enumerable.Range(0, 512).Select(i => i % 2 == 0 ? .2f : -.1f).ToArray();
        var output = new float[512];
        effect.Process(input, output, 256);
        if (pan == .5f && width == 1)
            Assert.Equal(input, output);
        if (pan == 0)
            Assert.Equal(0, output[1]);
        if (pan == 1)
            Assert.Equal(0, output[0]);
        if (width == 0)
            Assert.Equal(output[0], output[1]);
        if (width == 2)
        {
            Assert.Equal(.35f, output[0], 6);
            Assert.Equal(-.25f, output[1], 6);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AmbisonicsEncodingAndConventionRoundTripMatchNative(int order)
    {
        using var renderer = new Renderer();
        var state = new Spatialization(new()
        {
            Position = Vector3.UnitX
        });
        using var effect = renderer.CreateAmbisonicsEncoder(Mono, state, order);
        using var context = new Native.Context();
        using var native = new Native.Effects.AmbisonicsEncode(context, new(48000, 256), new()
        {
            MaxOrder = order
        });
        var channels = (order + 1) * (order + 1);
        using var input = new Native.AudioBuffer(context, 1, 256);
        using var output = new Native.AudioBuffer(context, channels, 256);
        var source = Signal(1);
        var expected = new float[256 * channels];
        var actual = new float[expected.Length];
        input.Deinterleave(source);
        native.Process(new()
        {
            Order = order,
            Direction = new()
            {
                X = 1
            }
        }, input, output);
        output.Interleave(expected);
        effect.Process(source, actual, 256);
        Equal(expected, actual);
        foreach (var convention in new[] { AmbisonicsConvention.SN3D, AmbisonicsConvention.FuMa })
        {
            using var convert = renderer.CreateAmbisonicsConverter(effect.OutputFormat, convention);
            using var restore = renderer.CreateAmbisonicsConverter(convert.OutputFormat, AmbisonicsConvention.N3D);
            var converted = new float[actual.Length];
            var restored = new float[actual.Length];
            convert.Process(actual, converted, 256);
            restore.Process(converted, restored, 256);
            Equal(actual, restored);
        }
    }

    [Fact]
    public void AmbisonicsInputCanReachARealStereoCallbackEndpoint()
    {
        using var encoderRenderer = new Renderer();
        var state = new Spatialization(new()
        {
            Position = Vector3.UnitX
        });
        using var encoder = encoderRenderer.CreateAmbisonicsEncoder(Mono, state, 3);
        var field = new float[256 * 16];
        encoder.Process(Signal(1), field, 256);
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        using var device = engine.CreateRenderDevice(Stereo);
        using var renderer = new Renderer();
        var builder = new Builder().Add(format => renderer.CreateAmbisonicsDecoder(format));
        using var source = device.CreateSource(new Clip(field, encoder.OutputFormat), new SourceOptions { RenderProcessing = builder.Build() });
        Assert.Equal(16, source.Format.Channels);
        Assert.Equal(2, source.RenderedFormat.Channels);
        Assert.Null(source.RenderedFormat.Ambisonics);
        source.Play();
        var output = new float[4096];
        device.Render(output);
        Assert.Contains(output, sample => Math.Abs(sample) > .0001f);
    }

    [Fact]
    public void MetadataPreventsAmbisonicsFromBeingSubmittedAsQuadSpeakers()
    {
        var format = Mono with
        {
            Channels = 4,
            Layout = ChannelLayout.Ambisonics,
            Ambisonics = new(1)
        };
        Assert.Throws<NotSupportedException>(() => format.WithEndpointChannels(2));
        Assert.Throws<ArgumentException>(() => new Clip(new float[16], format with { Layout = ChannelLayout.Quad }));
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        using var device = engine.CreateRenderDevice(Stereo);
        Assert.Throws<NotSupportedException>(() => device.CreateSource(new Clip(new float[256 * 4], format)));
        Assert.True(device.TryGetFeature<SoundFlow.Playback.IPreparedClips>(out var prepared));
        Assert.Throws<NotSupportedException>(() => prepared.Prepare(new Clip(new float[1024], format)));
        using var provider = new Clip(new float[1024], format).Open();
        Assert.Throws<NotSupportedException>(() => new ConvertedDataProvider(provider, format, format with { Ambisonics = new(1, AmbisonicsConvention.SN3D) }));
    }
}
