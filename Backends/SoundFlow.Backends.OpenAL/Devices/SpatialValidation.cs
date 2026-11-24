using System.Numerics;
using SoundFlow.Features;

namespace SoundFlow.Backends.OpenAL.Devices;

/// <summary>Central spatial parameter validation shared by source and listener adapters.</summary>
internal static class SpatialValidation
{
    internal static void Vector(Vector3 value, string name)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z)) throw new ArgumentOutOfRangeException(name);
    }
    internal static void Number(float value, float minimum, float maximum, string name)
    {
        if (!float.IsFinite(value) || value < minimum || value > maximum) throw new ArgumentOutOfRangeException(name);
    }
}
