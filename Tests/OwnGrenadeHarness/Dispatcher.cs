using EFT;
using UnityEngine;

public class GlobalEventDispatcher
{
    public event Action<Grenade, Vector3, Vector3, float>? OnGrenadeThrow;
    public event Action<Vector3, string, bool, float, float, int>? OnGrenadeExplosive;
    public int Listeners => OnGrenadeThrow?.GetInvocationList().Length ?? 0;
    public void ThrowGrenade(Grenade g, Vector3 pos, Vector3 force, float mass) =>
        OnGrenadeThrow?.Invoke(g, pos, force, mass);
    public void Explode(string owner, int id) => OnGrenadeExplosive?.Invoke(default, owner, false, 6, 0, id);
}

namespace Comfort.Common
{
    public static class Singleton<T> where T : class { public static T? Instance; }
}
