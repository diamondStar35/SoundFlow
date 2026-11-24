using Engine = SoundFlow.Engine;
using SoundFlow.Abstracts;
using Xunit;

namespace SoundFlow.Tests;

public sealed class BackendBoundaryTests
{
    [Fact]
    public void CoreDoesNotContainOrReferenceANativeBackend()
    {
        var assembly = typeof(Engine).Assembly;
        Assert.Null(assembly.GetType("SoundFlow.Backends.MiniAudio.Engine"));
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), a => a.Name!.Contains("MiniAudio"));
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), a => a.Name!.Contains("OpenAL"));
    }
}
