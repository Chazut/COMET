using System;
using EFT;
using UnityEngine;

namespace Comet.Api;

public static class MovementRecoveryNetwork
{
    // Fika optional bridge, authority checked again by the bridge. No client-side recovery decisions.
    public static Func<Player, Vector3, bool>? Teleport;
}
