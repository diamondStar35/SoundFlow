using BackendException = SoundFlow.Backends.OpenAL.BackendException;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace SoundFlow.Backends.OpenAL.Interop;

/// <summary>Owns one loaded OpenAL Soft library. Calls are made only on a context owner thread, except device enumeration/capture.</summary>
internal sealed unsafe class Functions : IDisposable
{
    private nint _library;
    public string LibraryPath { get; }
    internal nint LibraryHandle => _library;
    public readonly delegate* unmanaged[Cdecl]<byte*, nint> alcOpenDevice;
    public readonly delegate* unmanaged[Cdecl]<nint, byte> alcCloseDevice;
    public readonly delegate* unmanaged[Cdecl]<nint, int*, nint> alcCreateContext;
    public readonly delegate* unmanaged[Cdecl]<nint, void> alcDestroyContext;
    public readonly delegate* unmanaged[Cdecl]<nint, byte> alcMakeContextCurrent;
    public readonly delegate* unmanaged[Cdecl]<nint, int> alcGetError;
    public readonly delegate* unmanaged[Cdecl]<nint, int, byte*> alcGetString;
    public readonly delegate* unmanaged[Cdecl]<nint, int, int, int*, void> alcGetIntegerv;
    public readonly delegate* unmanaged[Cdecl]<nint, byte*, byte> alcIsExtensionPresent;
    public readonly delegate* unmanaged[Cdecl]<nint, byte*, nint> alcGetProcAddress;
    public readonly delegate* unmanaged[Cdecl]<byte*, uint, int, int, nint> alcCaptureOpenDevice;
    public readonly delegate* unmanaged[Cdecl]<nint, byte> alcCaptureCloseDevice;
    public readonly delegate* unmanaged[Cdecl]<nint, void> alcCaptureStart;
    public readonly delegate* unmanaged[Cdecl]<nint, void> alcCaptureStop;
    public readonly delegate* unmanaged[Cdecl]<nint, void*, int, void> alcCaptureSamples;
    public readonly delegate* unmanaged[Cdecl]<int> alGetError;
    public readonly delegate* unmanaged[Cdecl]<int, byte*> alGetString;
    public readonly delegate* unmanaged[Cdecl]<byte*, nint> alGetProcAddress;
    public readonly delegate* unmanaged[Cdecl]<byte*, byte> alIsExtensionPresent;
    public readonly delegate* unmanaged[Cdecl]<int, uint*, void> alGenSources;
    public readonly delegate* unmanaged[Cdecl]<int, uint*, void> alDeleteSources;
    public readonly delegate* unmanaged[Cdecl]<uint, int, int, void> alSourcei;
    public readonly delegate* unmanaged[Cdecl]<uint, int, float, void> alSourcef;
    public readonly delegate* unmanaged[Cdecl]<uint, int, float, float, float, void> alSource3f;
    public readonly delegate* unmanaged[Cdecl]<uint, int, int, int, int, void> alSource3i;
    public readonly delegate* unmanaged[Cdecl]<uint, int, int*, void> alGetSourcei;
    public readonly delegate* unmanaged[Cdecl]<uint, int, float*, void> alGetSourcef;
    public readonly delegate* unmanaged[Cdecl]<uint, void> alSourcePlay;
    public readonly delegate* unmanaged[Cdecl]<uint, void> alSourcePause;
    public readonly delegate* unmanaged[Cdecl]<uint, void> alSourceStop;
    public readonly delegate* unmanaged[Cdecl]<int, uint*, void> alGenBuffers;
    public readonly delegate* unmanaged[Cdecl]<int, uint*, void> alDeleteBuffers;
    public readonly delegate* unmanaged[Cdecl]<uint, int, void*, int, int, void> alBufferData;
    public readonly delegate* unmanaged[Cdecl]<uint, int, uint*, void> alSourceQueueBuffers;
    public readonly delegate* unmanaged[Cdecl]<uint, int, uint*, void> alSourceUnqueueBuffers;
    public readonly delegate* unmanaged[Cdecl]<int, float, void> alListenerf;
    public readonly delegate* unmanaged[Cdecl]<int, float, float, float, void> alListener3f;
    public readonly delegate* unmanaged[Cdecl]<int, float*, void> alListenerfv;
    public readonly delegate* unmanaged[Cdecl]<int, void> alDistanceModel;
    public readonly delegate* unmanaged[Cdecl]<float, void> alDopplerFactor;
    public readonly delegate* unmanaged[Cdecl]<float, void> alSpeedOfSound;
    public readonly delegate* unmanaged[Cdecl]<nint, byte> alcSetThreadContext;
    public readonly delegate* unmanaged[Cdecl]<byte*, nint> alcLoopbackOpenDeviceSOFT;
    public readonly delegate* unmanaged[Cdecl]<nint, int, int, int, byte> alcIsRenderFormatSupportedSOFT;
    public readonly delegate* unmanaged[Cdecl]<nint, void*, int, void> alcRenderSamplesSOFT;
    public readonly delegate* unmanaged[Cdecl]<nint, int*, byte> alcResetDeviceSOFT;
    public readonly delegate* unmanaged[Cdecl]<nint, byte*, int*, byte> alcReopenDeviceSOFT;
    public readonly delegate* unmanaged[Cdecl]<nint, int, int, byte*> alcGetStringiSOFT;
    public readonly delegate* unmanaged[Cdecl]<nint, void> alcDevicePauseSOFT;
    public readonly delegate* unmanaged[Cdecl]<nint, void> alcDeviceResumeSOFT;
    public readonly delegate* unmanaged[Cdecl]<int, uint*, void> alGenEffects;
    public readonly delegate* unmanaged[Cdecl]<int, uint*, void> alDeleteEffects;
    public readonly delegate* unmanaged[Cdecl]<uint, int, int, void> alEffecti;
    public readonly delegate* unmanaged[Cdecl]<uint, int, float, void> alEffectf;
    public readonly delegate* unmanaged[Cdecl]<uint, int, float*, void> alEffectfv;
    public readonly delegate* unmanaged[Cdecl]<int, uint*, void> alGenFilters;
    public readonly delegate* unmanaged[Cdecl]<int, uint*, void> alDeleteFilters;
    public readonly delegate* unmanaged[Cdecl]<uint, int, int, void> alFilteri;
    public readonly delegate* unmanaged[Cdecl]<uint, int, float, void> alFilterf;
    public readonly delegate* unmanaged[Cdecl]<int, uint*, void> alGenAuxiliaryEffectSlots;
    public readonly delegate* unmanaged[Cdecl]<int, uint*, void> alDeleteAuxiliaryEffectSlots;
    public readonly delegate* unmanaged[Cdecl]<uint, int, int, void> alAuxiliaryEffectSloti;
    public readonly delegate* unmanaged[Cdecl]<uint, int, float, void> alAuxiliaryEffectSlotf;
    public readonly delegate* unmanaged[Cdecl]<void> alDeferUpdatesSOFT;
    public readonly delegate* unmanaged[Cdecl]<void> alProcessUpdatesSOFT;
    public readonly delegate* unmanaged[Cdecl]<uint, int, float*, void> alSourcefv;
    public readonly delegate* unmanaged[Cdecl]<int, int> alGetInteger;
    public readonly delegate* unmanaged[Cdecl]<int, void> alEnable;
    public readonly delegate* unmanaged[Cdecl]<int, uint*, void> alSourcePlayv;
    public readonly delegate* unmanaged[Cdecl]<int, uint*, void> alSourcePausev;
    public readonly delegate* unmanaged[Cdecl]<int, uint*, void> alSourceStopv;
    public readonly delegate* unmanaged[Cdecl]<int, uint*, void> alSourceRewindv;
    public readonly delegate* unmanaged[Cdecl]<int, int, byte*> alGetStringiSOFT;
    public readonly delegate* unmanaged[Cdecl]<uint, int, long*, void> alGetSourcei64vSOFT;
    public readonly delegate* unmanaged[Cdecl]<nint, int, int, long*, void> alcGetInteger64vSOFT;
    public readonly delegate* unmanaged[Cdecl]<uint, long, void> alSourcePlayAtTimeSOFT;
    public readonly delegate* unmanaged[Cdecl]<int, uint*, long, void> alSourcePlayAtTimevSOFT;
    public readonly delegate* unmanaged[Cdecl]<uint, int, int, delegate* unmanaged[Cdecl]<nint, void*, int, int>, nint, void> alBufferCallbackSOFT;
    public readonly delegate* unmanaged[Cdecl]<int, int*, byte, void> alEventControlSOFT;
    public readonly delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<int, uint, uint, int, byte*, nint, void>, nint, void> alEventCallbackSOFT;
    public readonly delegate* unmanaged[Cdecl]<int, int, int> alcEventIsSupportedSOFT;
    public readonly delegate* unmanaged[Cdecl]<int, int*, byte, byte> alcEventControlSOFT;
    public readonly delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<int, int, nint, int, byte*, nint, void>, nint, void> alcEventCallbackSOFT;
    public readonly delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<int, int, uint, int, int, byte*, nint, void>, nint, void> alDebugMessageCallbackEXT;
    public readonly delegate* unmanaged[Cdecl]<int, int, int, int, uint*, byte, void> alDebugMessageControlEXT;
    public readonly delegate* unmanaged[Cdecl]<int, int, uint, int, int, byte*, void> alDebugMessageInsertEXT;
    public readonly delegate* unmanaged[Cdecl]<int, uint, int, byte*, void> alObjectLabelEXT;
    public Functions(string? explicitPath)
    {
        var filename = OperatingSystem.IsWindows() ? "soft_oal.dll" : OperatingSystem.IsMacOS() ? "libopenal.dylib" : "libopenal.so";
        string[] candidates = explicitPath is not null ? [Path.GetFullPath(explicitPath)] :
        [Path.Combine(AppContext.BaseDirectory, "lib", filename), Path.Combine(AppContext.BaseDirectory, filename),
         Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", filename)];
#if SOUNDFLOW_IOS_FRAMEWORK_IMPORTS
        candidates = ["@rpath/soft_oal.framework/soft_oal"];
#endif
        if (OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst())
            candidates = explicitPath != null ? [explicitPath] :
                ["@rpath/soft_oal.framework/soft_oal", Path.Combine(AppContext.BaseDirectory, "Frameworks", "soft_oal.framework", "soft_oal")];
        foreach (var candidate in candidates)
        {
            if (NativeLibrary.TryLoad(candidate, out _library)) { LibraryPath = candidate; break; }
        }
        if (_library == 0 && OperatingSystem.IsAndroid())
            NativeLibrary.TryLoad("openal", typeof(Functions).Assembly, DllImportSearchPath.SafeDirectories, out _library);
        if (_library == 0) throw new DllNotFoundException("OpenAL Soft could not be loaded. Attempted: " + string.Join(", ", candidates));
        LibraryPath ??= filename;
        try
        {
            alcOpenDevice = (delegate* unmanaged[Cdecl]<byte*, nint>)NativeLibrary.GetExport(_library, "alcOpenDevice");
            alcCloseDevice = (delegate* unmanaged[Cdecl]<nint, byte>)NativeLibrary.GetExport(_library, "alcCloseDevice");
            alcCreateContext = (delegate* unmanaged[Cdecl]<nint, int*, nint>)NativeLibrary.GetExport(_library, "alcCreateContext");
            alcDestroyContext = (delegate* unmanaged[Cdecl]<nint, void>)NativeLibrary.GetExport(_library, "alcDestroyContext");
            alcMakeContextCurrent = (delegate* unmanaged[Cdecl]<nint, byte>)NativeLibrary.GetExport(_library, "alcMakeContextCurrent");
            alcGetError = (delegate* unmanaged[Cdecl]<nint, int>)NativeLibrary.GetExport(_library, "alcGetError");
            alcGetString = (delegate* unmanaged[Cdecl]<nint, int, byte*>)NativeLibrary.GetExport(_library, "alcGetString");
            alcGetIntegerv = (delegate* unmanaged[Cdecl]<nint, int, int, int*, void>)NativeLibrary.GetExport(_library, "alcGetIntegerv");
            alcIsExtensionPresent = (delegate* unmanaged[Cdecl]<nint, byte*, byte>)NativeLibrary.GetExport(_library, "alcIsExtensionPresent");
            alcGetProcAddress = (delegate* unmanaged[Cdecl]<nint, byte*, nint>)NativeLibrary.GetExport(_library, "alcGetProcAddress");
            alcCaptureOpenDevice = (delegate* unmanaged[Cdecl]<byte*, uint, int, int, nint>)NativeLibrary.GetExport(_library, "alcCaptureOpenDevice");
            alcCaptureCloseDevice = (delegate* unmanaged[Cdecl]<nint, byte>)NativeLibrary.GetExport(_library, "alcCaptureCloseDevice");
            alcCaptureStart = (delegate* unmanaged[Cdecl]<nint, void>)NativeLibrary.GetExport(_library, "alcCaptureStart");
            alcCaptureStop = (delegate* unmanaged[Cdecl]<nint, void>)NativeLibrary.GetExport(_library, "alcCaptureStop");
            alcCaptureSamples = (delegate* unmanaged[Cdecl]<nint, void*, int, void>)NativeLibrary.GetExport(_library, "alcCaptureSamples");
            alGetError = (delegate* unmanaged[Cdecl]<int>)NativeLibrary.GetExport(_library, "alGetError");
            alGetString = (delegate* unmanaged[Cdecl]<int, byte*>)NativeLibrary.GetExport(_library, "alGetString");
            alGetProcAddress = (delegate* unmanaged[Cdecl]<byte*, nint>)NativeLibrary.GetExport(_library, "alGetProcAddress");
            alIsExtensionPresent = (delegate* unmanaged[Cdecl]<byte*, byte>)NativeLibrary.GetExport(_library, "alIsExtensionPresent");
            alGenSources = (delegate* unmanaged[Cdecl]<int, uint*, void>)NativeLibrary.GetExport(_library, "alGenSources");
            alDeleteSources = (delegate* unmanaged[Cdecl]<int, uint*, void>)NativeLibrary.GetExport(_library, "alDeleteSources");
            alSourcei = (delegate* unmanaged[Cdecl]<uint, int, int, void>)NativeLibrary.GetExport(_library, "alSourcei");
            alSourcef = (delegate* unmanaged[Cdecl]<uint, int, float, void>)NativeLibrary.GetExport(_library, "alSourcef");
            alSource3f = (delegate* unmanaged[Cdecl]<uint, int, float, float, float, void>)NativeLibrary.GetExport(_library, "alSource3f");
            alSource3i = (delegate* unmanaged[Cdecl]<uint, int, int, int, int, void>)NativeLibrary.GetExport(_library, "alSource3i");
            alGetSourcei = (delegate* unmanaged[Cdecl]<uint, int, int*, void>)NativeLibrary.GetExport(_library, "alGetSourcei");
            alGetSourcef = (delegate* unmanaged[Cdecl]<uint, int, float*, void>)NativeLibrary.GetExport(_library, "alGetSourcef");
            alSourcePlay = (delegate* unmanaged[Cdecl]<uint, void>)NativeLibrary.GetExport(_library, "alSourcePlay");
            alSourcePause = (delegate* unmanaged[Cdecl]<uint, void>)NativeLibrary.GetExport(_library, "alSourcePause");
            alSourceStop = (delegate* unmanaged[Cdecl]<uint, void>)NativeLibrary.GetExport(_library, "alSourceStop");
            alGenBuffers = (delegate* unmanaged[Cdecl]<int, uint*, void>)NativeLibrary.GetExport(_library, "alGenBuffers");
            alDeleteBuffers = (delegate* unmanaged[Cdecl]<int, uint*, void>)NativeLibrary.GetExport(_library, "alDeleteBuffers");
            alBufferData = (delegate* unmanaged[Cdecl]<uint, int, void*, int, int, void>)NativeLibrary.GetExport(_library, "alBufferData");
            alSourceQueueBuffers = (delegate* unmanaged[Cdecl]<uint, int, uint*, void>)NativeLibrary.GetExport(_library, "alSourceQueueBuffers");
            alSourceUnqueueBuffers = (delegate* unmanaged[Cdecl]<uint, int, uint*, void>)NativeLibrary.GetExport(_library, "alSourceUnqueueBuffers");
            alListenerf = (delegate* unmanaged[Cdecl]<int, float, void>)NativeLibrary.GetExport(_library, "alListenerf");
            alListener3f = (delegate* unmanaged[Cdecl]<int, float, float, float, void>)NativeLibrary.GetExport(_library, "alListener3f");
            alListenerfv = (delegate* unmanaged[Cdecl]<int, float*, void>)NativeLibrary.GetExport(_library, "alListenerfv");
            alDistanceModel = (delegate* unmanaged[Cdecl]<int, void>)NativeLibrary.GetExport(_library, "alDistanceModel");
            alDopplerFactor = (delegate* unmanaged[Cdecl]<float, void>)NativeLibrary.GetExport(_library, "alDopplerFactor");
            alSpeedOfSound = (delegate* unmanaged[Cdecl]<float, void>)NativeLibrary.GetExport(_library, "alSpeedOfSound");
            alcSetThreadContext = (delegate* unmanaged[Cdecl]<nint, byte>)FindExtension("alcSetThreadContext");
            alcLoopbackOpenDeviceSOFT = (delegate* unmanaged[Cdecl]<byte*, nint>)FindExtension("alcLoopbackOpenDeviceSOFT");
            alcIsRenderFormatSupportedSOFT = (delegate* unmanaged[Cdecl]<nint, int, int, int, byte>)FindExtension("alcIsRenderFormatSupportedSOFT");
            alcRenderSamplesSOFT = (delegate* unmanaged[Cdecl]<nint, void*, int, void>)FindExtension("alcRenderSamplesSOFT");
            alcResetDeviceSOFT = (delegate* unmanaged[Cdecl]<nint, int*, byte>)FindExtension("alcResetDeviceSOFT");
            alcReopenDeviceSOFT = (delegate* unmanaged[Cdecl]<nint, byte*, int*, byte>)FindExtension("alcReopenDeviceSOFT");
            alcGetStringiSOFT = (delegate* unmanaged[Cdecl]<nint, int, int, byte*>)FindExtension("alcGetStringiSOFT");
            alcDevicePauseSOFT = (delegate* unmanaged[Cdecl]<nint, void>)FindExtension("alcDevicePauseSOFT");
            alcDeviceResumeSOFT = (delegate* unmanaged[Cdecl]<nint, void>)FindExtension("alcDeviceResumeSOFT");
            alDebugMessageCallbackEXT = (delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<int, int, uint, int, int, byte*, nint, void>, nint, void>)FindExtension("alDebugMessageCallbackEXT");
            alDebugMessageControlEXT = (delegate* unmanaged[Cdecl]<int, int, int, int, uint*, byte, void>)FindExtension("alDebugMessageControlEXT");
            alDebugMessageInsertEXT = (delegate* unmanaged[Cdecl]<int, int, uint, int, int, byte*, void>)FindExtension("alDebugMessageInsertEXT");
            alBufferCallbackSOFT = (delegate* unmanaged[Cdecl]<uint, int, int, delegate* unmanaged[Cdecl]<nint, void*, int, int>, nint, void>)FindExtension("alBufferCallbackSOFT");
            alObjectLabelEXT = (delegate* unmanaged[Cdecl]<int, uint, int, byte*, void>)FindExtension("alObjectLabelEXT");
            alGenEffects = (delegate* unmanaged[Cdecl]<int, uint*, void>)FindExtension("alGenEffects");
            alDeleteEffects = (delegate* unmanaged[Cdecl]<int, uint*, void>)FindExtension("alDeleteEffects");
            alEffecti = (delegate* unmanaged[Cdecl]<uint, int, int, void>)FindExtension("alEffecti");
            alEffectf = (delegate* unmanaged[Cdecl]<uint, int, float, void>)FindExtension("alEffectf");
            alEffectfv = (delegate* unmanaged[Cdecl]<uint, int, float*, void>)FindExtension("alEffectfv");
            alGenFilters = (delegate* unmanaged[Cdecl]<int, uint*, void>)FindExtension("alGenFilters");
            alDeleteFilters = (delegate* unmanaged[Cdecl]<int, uint*, void>)FindExtension("alDeleteFilters");
            alFilteri = (delegate* unmanaged[Cdecl]<uint, int, int, void>)FindExtension("alFilteri");
            alFilterf = (delegate* unmanaged[Cdecl]<uint, int, float, void>)FindExtension("alFilterf");
            alGenAuxiliaryEffectSlots = (delegate* unmanaged[Cdecl]<int, uint*, void>)FindExtension("alGenAuxiliaryEffectSlots");
            alDeleteAuxiliaryEffectSlots = (delegate* unmanaged[Cdecl]<int, uint*, void>)FindExtension("alDeleteAuxiliaryEffectSlots");
            alAuxiliaryEffectSloti = (delegate* unmanaged[Cdecl]<uint, int, int, void>)FindExtension("alAuxiliaryEffectSloti");
            alAuxiliaryEffectSlotf = (delegate* unmanaged[Cdecl]<uint, int, float, void>)FindExtension("alAuxiliaryEffectSlotf");
            alDeferUpdatesSOFT = (delegate* unmanaged[Cdecl]<void>)FindExtension("alDeferUpdatesSOFT");
            alProcessUpdatesSOFT = (delegate* unmanaged[Cdecl]<void>)FindExtension("alProcessUpdatesSOFT");
            alEventControlSOFT = (delegate* unmanaged[Cdecl]<int, int*, byte, void>)FindExtension("alEventControlSOFT");
            alEventCallbackSOFT = (delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<int, uint, uint, int, byte*, nint, void>, nint, void>)FindExtension("alEventCallbackSOFT");
            alcEventIsSupportedSOFT = (delegate* unmanaged[Cdecl]<int, int, int>)FindExtension("alcEventIsSupportedSOFT");
            alcEventControlSOFT = (delegate* unmanaged[Cdecl]<int, int*, byte, byte>)FindExtension("alcEventControlSOFT");
            alcEventCallbackSOFT = (delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<int, int, nint, int, byte*, nint, void>, nint, void>)FindExtension("alcEventCallbackSOFT");
            alSourcefv = (delegate* unmanaged[Cdecl]<uint, int, float*, void>)NativeLibrary.GetExport(_library, "alSourcefv");
            alGetInteger = (delegate* unmanaged[Cdecl]<int, int>)NativeLibrary.GetExport(_library, "alGetInteger");
            alEnable = (delegate* unmanaged[Cdecl]<int, void>)NativeLibrary.GetExport(_library, "alEnable");
            alSourcePlayv = (delegate* unmanaged[Cdecl]<int, uint*, void>)NativeLibrary.GetExport(_library, "alSourcePlayv");
            alSourcePausev = (delegate* unmanaged[Cdecl]<int, uint*, void>)NativeLibrary.GetExport(_library, "alSourcePausev");
            alSourceStopv = (delegate* unmanaged[Cdecl]<int, uint*, void>)NativeLibrary.GetExport(_library, "alSourceStopv");
            alSourceRewindv = (delegate* unmanaged[Cdecl]<int, uint*, void>)NativeLibrary.GetExport(_library, "alSourceRewindv");
            alGetStringiSOFT = (delegate* unmanaged[Cdecl]<int, int, byte*>)FindExtension("alGetStringiSOFT");
            alGetSourcei64vSOFT = (delegate* unmanaged[Cdecl]<uint, int, long*, void>)FindExtension("alGetSourcei64vSOFT");
            alcGetInteger64vSOFT = (delegate* unmanaged[Cdecl]<nint, int, int, long*, void>)FindExtension("alcGetInteger64vSOFT");
            alSourcePlayAtTimeSOFT = (delegate* unmanaged[Cdecl]<uint, long, void>)FindExtension("alSourcePlayAtTimeSOFT");
            alSourcePlayAtTimevSOFT = (delegate* unmanaged[Cdecl]<int, uint*, long, void>)FindExtension("alSourcePlayAtTimevSOFT");
        }
        catch { NativeLibrary.Free(_library); throw; }
    }

    private nint FindExtension(string name)
    {
        if (NativeLibrary.TryGetExport(_library, name, out var result)) return result;
        var bytes = Encoding.UTF8.GetBytes(name + "\0");
        fixed (byte* pointer = bytes) return alcGetProcAddress(0, pointer);
    }

    public bool HasAlExtension(string extension)
    {
        var bytes = Encoding.UTF8.GetBytes(extension + "\0");
        fixed (byte* pointer = bytes) return alIsExtensionPresent(pointer) != 0;
    }
    public bool HasAlcExtension(nint device, string extension)
    {
        var bytes = Encoding.UTF8.GetBytes(extension + "\0");
        fixed (byte* pointer = bytes) return alcIsExtensionPresent(device, pointer) != 0;
    }
    public static string Text(byte* value) => value == null ? string.Empty : Marshal.PtrToStringUTF8((nint)value) ?? string.Empty;
    public void CheckAl(string operation)
    {
        var code = alGetError();
        if (code != 0) throw new BackendException(operation, code, LibraryPath);
    }
    public void CheckAlc(nint device, string operation)
    {
        var code = alcGetError(device);
        if (code != 0) throw new BackendException(operation, code, LibraryPath);
    }
    public int SourceInteger(uint source, int parameter)
    {
        int result; alGetSourcei(source, parameter, &result); CheckAl("Query source"); return result;
    }
    public int DeviceInteger(nint device, int parameter)
    {
        int result; alcGetIntegerv(device, parameter, 1, &result); CheckAlc(device, "Query device"); return result;
    }
    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _library, 0);
        if (handle != 0) NativeLibrary.Free(handle);
    }
}
