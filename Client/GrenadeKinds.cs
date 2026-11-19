using EFT;

namespace Comet.Client;

internal static class GrenadeKinds
{
    // Same two smoke representations as zzap's NeverReacts. ThrowType alone
    // does not identify every smoke projectile in EFT or content mods.
    internal static bool IsSmoke(Grenade grenade) =>
        grenade is SmokeGrenade
        || grenade.GrenadeSettings.CollisionSound == GrenadeSettings.CollisionSounds.smoke;
}
