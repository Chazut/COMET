using System;
using EFT;
using EFT.Interactive;
using UnityEngine;

namespace Comet.Client;

public sealed partial class Plugin
{
    private readonly Collider[] leafHits = new Collider[32];
    private int? leafMask;

    // Voxel links span rooms and floors. Query the physical leaves touching the bot or its route.
    private Door? NearestPhysicalLeaf(
        Vector3 start, Vector3 end, float radius, Func<Door, bool>? eligible, out float distance
    )
    {
        int mask = leafMask ??=
            (1 << LayersMaskController.DoorLayer) | LayerMask.GetMask("DoorLowPolyCollider", "LowPolyCollider");
        int count = (end - start).sqrMagnitude == 0
            ? Physics.OverlapSphereNonAlloc(start, radius, leafHits, mask, QueryTriggerInteraction.Ignore)
            : Physics.OverlapCapsuleNonAlloc(start, end, radius, leafHits, mask, QueryTriggerInteraction.Ignore);
        distance = float.PositiveInfinity;
        Door? nearest = null;
        try
        {
            for (int i = 0; i < count; i++)
            {
                var hit = leafHits[i];
                if (hit == null)
                    continue;
                var door = hit.GetComponentInParent<Door>();
                if (door == null || (eligible != null && !eligible(door)))
                    continue;
                float separation = Vector3.Distance(hit.bounds.ClosestPoint(start), start);
                if (separation < distance)
                {
                    distance = separation;
                    nearest = door;
                }
            }
            return nearest;
        }
        finally
        {
            Array.Clear(leafHits, 0, count);
        }
    }
}
