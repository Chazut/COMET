using System;
using SAIN.Components;
namespace Comet.Client;
// postfix: new HarmonyMethod(typeof(Plugin), nameof(CloseVisibleAfter))
internal class VisionProbe {internal BotComponent Bot=new();internal bool IsVisible;}
public sealed partial class Plugin {
 internal int Acquisitions;
 internal void CloseVisibleAfter(VisionProbe __instance,bool __state){Acquisitions++;}
}
