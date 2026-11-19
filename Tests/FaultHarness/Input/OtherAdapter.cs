using SAIN.Components;

namespace Comet.Client;

public sealed partial class Plugin
{
    internal void Healthy(BotComponent bot)
    {
        HealthyCalls++;
    }

    internal void Depends(BotComponent bot)
    {
        Sample(bot);
        DependencyCalls++;
    }

    internal void Update()
    {
        foreach (var b in states.Keys)
        {
            Sample(b);
            Healthy(b);
        }
    }

    internal bool ThrowGlobal;

    internal void UpdateDoorMotion()
    {
        if (ThrowGlobal)
            throw new InvalidOperationException("global service failure");
    }

    // prefix: new HarmonyMethod(typeof(Plugin), nameof(RefPrefix))
    internal bool RefPrefix(BotComponent bot, ref int value, ref bool __result, out int __state)
    {
        value = 999;
        __result = false;
        __state = 5;
        Sample(bot);
        return false;
    }
}
