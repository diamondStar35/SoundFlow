using DistanceModel = SoundFlow.Spatial.DistanceModel;
using Functions = SoundFlow.Backends.OpenAL.Interop.Functions;
using IScene = SoundFlow.Spatial.IScene;
using Listener = SoundFlow.Spatial.Listener;
using System.Numerics;
using SoundFlow.Features;

namespace SoundFlow.Backends.OpenAL.Devices;

internal sealed unsafe partial class Context : IScene, IHrtfAudio
{
    private Listener _listener = new();
    private DistanceModel _distanceModel = DistanceModel.InverseClamped;
    private float _doppler = 1;
    private float _speedOfSound = 343.3f;
    private float _metresPerUnit = 1;
    public float MetresPerUnit
    {
        get => _metresPerUnit;
        set
        {
            SpatialValidation.Number(value, float.Epsilon, float.MaxValue, nameof(value));
            Worker.Invoke(() =>
            {
                if (!HasEfx) throw new NotSupportedException("Environmental distance units require EFX.");
                Al.alListenerf(0x20004, value); Al.CheckAl("Set environmental metres per unit"); _metresPerUnit = value;
            });
        }
    }
    private IReadOnlyList<string> _hrtfDatasets = Array.Empty<string>();
    public Listener Listener
    {
        get => _listener;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            SpatialValidation.Vector(value.Position, nameof(value.Position));
            SpatialValidation.Vector(value.Velocity, nameof(value.Velocity));
            SpatialValidation.Vector(value.Forward, nameof(value.Forward));
            SpatialValidation.Vector(value.Up, nameof(value.Up));
            if (value.Forward.LengthSquared() < 1e-12f || value.Up.LengthSquared() < 1e-12f ||
                Vector3.Cross(Vector3.Normalize(value.Forward), Vector3.Normalize(value.Up)).LengthSquared() < 1e-6f)
                throw new ArgumentException("Forward and up must be nonzero, nonparallel directions.", nameof(value));
            var pose = value with { Forward = Vector3.Normalize(value.Forward), Up = Vector3.Normalize(value.Up) };
            Worker.Invoke(() =>
            {
                ApplyNativeUpdates(() =>
                {
                    Al.alListener3f(0x1004, pose.Position.X, pose.Position.Y, pose.Position.Z);
                    Al.alListener3f(0x1006, pose.Velocity.X, pose.Velocity.Y, pose.Velocity.Z);
                    float* orientation = stackalloc float[6] { pose.Forward.X, pose.Forward.Y, pose.Forward.Z, pose.Up.X, pose.Up.Y, pose.Up.Z };
                    Al.alListenerfv(0x100F, orientation);
                    Al.CheckAl("Set listener pose");
                    _listener = pose;
                });
            });
        }
    }
    public DistanceModel DistanceModel
    {
        get => _distanceModel;
        set
        {
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            Worker.Invoke(() => { ApplyNativeUpdates(() =>
                {
                    Al.alDistanceModel(DistanceToken(value));
                    foreach (var source in Sources) source.ApplySceneDistance(value);
                    Al.CheckAl("Set distance model");
                    _distanceModel = value;
                }); });
        }
    }
    public float DopplerFactor
    {
        get => _doppler;
        set { SpatialValidation.Number(value, 0, float.MaxValue, nameof(value)); Worker.Invoke(() => { Al.alDopplerFactor(value); Al.CheckAl("Set Doppler factor"); _doppler = value; }); }
    }
    public float SpeedOfSound
    {
        get => _speedOfSound;
        set { SpatialValidation.Number(value, float.Epsilon, float.MaxValue, nameof(value)); Worker.Invoke(() => { Al.alSpeedOfSound(value); Al.CheckAl("Set speed of sound"); _speedOfSound = value; }); }
    }
    public IReadOnlyList<string> Datasets => _hrtfDatasets;
    public string Status => Worker.Invoke(HrtfStatus);
    private void InitializeHrtf()
    {
        if (!HasHrtf || Al.alcGetStringiSOFT == null) return;
        var count = Al.DeviceInteger(Device, 0x1994);
        var datasets = new string[count];
        for (var i = 0; i < count; i++) datasets[i] = Functions.Text(Al.alcGetStringiSOFT(Device, 0x1995, i));
        _hrtfDatasets = Array.AsReadOnly(datasets);
    }
    private string HrtfStatus() => Al.DeviceInteger(Device, 0x1993) switch
    {
        0 => "Disabled", 1 => "Enabled", 2 => "Denied", 3 => "Required", 4 => "Headphones detected", 5 => "Unsupported output format", _ => "Unknown"
    };
    public void Configure(HrtfMode mode, int? dataset = null)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (dataset.HasValue && (dataset < 0 || dataset >= Datasets.Count)) throw new ArgumentOutOfRangeException(nameof(dataset));
        Worker.Invoke(() =>
        {
            EnsureOutsideBatch();
            var attributes = Attributes(hrtfMode: mode, hrtfDataset: dataset);
            fixed (int* pointer = attributes)
                if (Al.alcResetDeviceSOFT(Device, pointer) == 0) throw new BackendException("Configure HRTF", Al.alcGetError(Device), Al.LibraryPath);
            _hrtfMode = mode; _hrtfDataset = dataset;
            MaximumSends = HasEfx ? Al.DeviceInteger(Device, 0x20003) : 0;
        });
    }
}
