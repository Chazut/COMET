using UnityEngine;

namespace Comet.Client;

internal static class CoverFeintJump
{
    internal const float MinimumForwardSpeed = 1.2f;

    internal static bool HasClearRunUp(Vector3 origin, Vector3 landing, Vector3 velocity)
    {
        var direction = landing - origin;
        direction.y = 0;
        velocity.y = 0;
        if (direction.sqrMagnitude < .0001f)
            return false;

        var head = Vector3.up * 1.7f;
        return Vector3.Dot(velocity, direction.normalized) >= MinimumForwardSpeed
            && Mathf.Abs(landing.y - origin.y) <= .25f
            && !Physics.Raycast(origin + head, Vector3.up, .6f, LayersMaskController.HighPolyWithTerrainMask)
            && !Physics.Raycast(landing + head, Vector3.up, .6f, LayersMaskController.HighPolyWithTerrainMask);
    }
}
