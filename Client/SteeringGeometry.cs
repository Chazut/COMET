using UnityEngine;

namespace Comet.Client;

internal static class SteeringGeometry
{
    // R_F / zzap SAINSteeringClass: keep the weapon at a level firing angle.
    internal static Vector3 ClampPitch(Vector3 from, Vector3 target, Vector3 forward)
    {
        var d = target - from;
        float h = new Vector3(d.x, 0, d.z).magnitude;
        if (h < .3f)
        {
            var f = new Vector3(forward.x, 0, forward.z);
            return from + (f.sqrMagnitude > .001f ? f.normalized : Vector3.forward) * 2;
        }
        float rise = Mathf.Max(h, 1.5f) * Mathf.Tan(10f * Mathf.Deg2Rad);
        return new(target.x, from.y + Mathf.Clamp(d.y, -rise, rise), target.z);
    }
}
