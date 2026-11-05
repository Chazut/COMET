using Comet.Core;
using SAIN.Components;
namespace UnityEngine {public static class Time {public static double unscaledTimeAsDouble;public static float realtimeSinceStartup=1;}}
namespace EFT
{
 public class Player{}
 public class BotOwner{public BotComponent Bot=new(); public T? GetComponent<T>() where T:class=>Bot as T;}
}
namespace SAIN.Components
{
 public class BotComponent {public bool Authority=true,Pmc=true;public EFT.Player Player=new(); public Cover Cover=new();}
 public class Cover{public object? CoverFinder;}
}
namespace Comet.Client
{
 internal static class NativeAdapter{internal static bool IsPmcAi(BotComponent b)=>b.Pmc;}
 internal class BotState
 {
  internal BotComponent Owner;internal string Id,ProfileId,Name="Test PMC",ActiveLabel="COMET:Test",Decision="Test";
  internal int RequestedFrame=1;internal bool OwnCommand;
  internal static bool FailCreation;
  internal BotState(BotComponent b,CometConfig c,string session,string? id=null){if(FailCreation)throw new Exception("injected fresh-state failure");Owner=b;Id=id??Guid.NewGuid().ToString("N");ProfileId=Id;}
 }
 public sealed partial class Plugin
 {
  partial void CoverScanFailure(BotComponent b,Exception ex,ref bool handled);
  partial void ConfigureCoverRetry(BotComponent b,ref Func<bool>? retry);
  private int CoverScanMode(BotComponent b)=>1;
  internal static Plugin? Instance;
  internal CometConfig? config=new();internal string session="c08-test";internal long sequence;
  internal readonly Dictionary<BotComponent,BotState> states=new();
  private readonly Dictionary<BotComponent,object> motionWatches=new();
  private Diagnostics? diagnostics;
  internal int GuardCalls;partial void GuardLifecycle(FaultScope scope,ref bool allowed){GuardCalls++;}
  internal bool Authority=true;
  internal TestLogger Logger=new();internal int CleanupCalls,OtherCleanupCalls;
  internal Plugin(){Instance=this;}
  internal BotComponent Add(){var b=new BotComponent();states[b]=new(b,config!,session);return b;}
  internal void Fresh()=>ResetFaultedBots();
  internal void Clear()=>faultIsolation.Clear();
  internal bool Disabled(BotComponent b)=>faultIsolation.BotDisabled(b);
  internal void Release(BotState s,string reason){CleanupCalls++;EndSample(s,reason);}
  internal void EndCrossfire(BotComponent b,BotState s,string r){OtherCleanupCalls++;}
  internal void EndPostCombat(BotComponent b,BotState s,string r,bool reset){}
  internal void DropFlash(BotComponent b,BotState s,string r,bool dead){}
  internal void StopCoverScan(object finder){}
 }
 internal class TestLogger{internal bool Throw;internal readonly List<string> Messages=new();internal void LogWarning(string text){if(Throw)throw new Exception("logger failed");Messages.Add(text);}}
}
