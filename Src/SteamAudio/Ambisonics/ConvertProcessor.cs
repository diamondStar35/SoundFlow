using SoundFlow.Structs;
using Native = global::SteamAudio;

namespace SoundFlow.SteamAudio.Ambisonics;

/// <summary>Converts Ambisonics ordering/normalization explicitly without changing order or channel count.</summary>
public sealed unsafe class ConvertProcessor : Processor
{
    private readonly nint _context;
    internal ConvertProcessor(Renderer renderer, AudioFormat format, AmbisonicsConvention convention)
        : base(renderer, format, Format.Convert(format, convention), new())
    {
        try
        {
            _context = renderer.Context.DangerousGetHandle();
            Warm();
        }
        catch
        {
            Dispose();
            throw;
        }
    }
    private protected override Native.Interop.AudioEffectState ApplyNative()
    {
        using var input = Input.Borrow();
        using var output = Output.Borrow();
        var source = new Native.Interop.AudioBuffer { NumChannels = Input.Channels, NumSamples = Input.Frames };
        var destination = new Native.Interop.AudioBuffer { NumChannels = Output.Channels, NumSamples = Output.Frames };
        float** sourceChannels = stackalloc float*[Input.Channels];
        float** targetChannels = stackalloc float*[Output.Channels];
        for (var i = 0; i < Input.Channels; i++)
        {
            sourceChannels[i] = (float*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref System.Runtime.InteropServices.MemoryMarshal.GetReference(input.Channel(i)));
            targetChannels[i] = (float*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref System.Runtime.InteropServices.MemoryMarshal.GetReference(output.Channel(i)));
        }
        source.Data = sourceChannels;
        destination.Data = targetChannels;
        Native.Interop.Api.AudioBufferConvertAmbisonics(new(_context), Format.Convention(InputFormat.Ambisonics!.Value.Convention),
            Format.Convention(OutputFormat.Ambisonics!.Value.Convention), &source, &destination);
        return Native.Interop.AudioEffectState.TailComplete;
    }
    private protected override Native.Interop.AudioEffectState DrainNative() => Native.Interop.AudioEffectState.TailComplete;
    private protected override int TailFrames => 0;
    private protected override void ResetNative()
    {
    }
    private protected override void ReleaseNative()
    {
    }
}
