using System;
using Comfort.Common;

namespace Comet.Client;

public sealed partial class Plugin
{
    private GlobalEventDispatcher? clearEvents;

    // EFT reads its event delegate inside ThrowGrenade. Subscribe before that read,
    // including the first throw on a newly created dispatcher.
    private static void BeforeGrenadeBroadcast(GlobalEventDispatcher __instance)
    {
        var p = Instance;
        if (p == null || p.world == null || p.config?.Enabled != true ||
            !p.config.OwnGrenadeSafety.Enabled || !p.Authority)
            return;
        try
        {
            p.EnsureClearEvents(__instance);
        }
        catch (Exception ex)
        {
            p.Logger.LogWarning("COMET grenade event subscription failed: " + ex.GetBaseException().Message);
        }
    }

    private void EnsureClearEvents(GlobalEventDispatcher? current = null)
    {
        current ??= Singleton<GlobalEventDispatcher>.Instance;
        if (ReferenceEquals(clearEvents, current))
            return;
        ClearRoomEvents();
        clearEvents = current;
        if (current != null)
        {
            current.OnGrenadeThrow += ClearProjectileThrown;
            current.OnGrenadeExplosive += ClearProjectileExplosion;
        }
    }

    private void ClearRoomEvents()
    {
        if (clearEvents != null)
        {
            clearEvents.OnGrenadeThrow -= ClearProjectileThrown;
            clearEvents.OnGrenadeExplosive -= ClearProjectileExplosion;
        }
        clearEvents = null;
        foreach (var link in clearOwnGrenades)
            link.Dispose();
        clearOwnGrenades.Clear();
    }
}
