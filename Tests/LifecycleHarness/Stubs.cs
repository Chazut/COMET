using Comet.Core;
using SAIN.Components;
namespace UnityEngine {public static class Time {public static double unscaledTimeAsDouble;public static float realtimeSinceStartup=1;}}
namespace EFT
{
 public enum WildSpawnType {pmcUSEC,pmcBEAR,assault,pmcBot}
 public class Player {public bool IsAI=true;public Health? HealthController=new();public Profile Profile=new();}
 public class Health {public bool IsAlive=true;}
 public class Profile {public Info Info=new();}
 public class Info {public Settings Settings=new();}
 public class Settings {public WildSpawnType Role=WildSpawnType.pmcUSEC;}
 public class RevivablePlayer:Player{public bool ThrowRead;private bool downed;public bool Downed{get{if(ThrowRead)throw new Exception("downed unavailable");return downed;}set=>downed=value;}}
 public class BotOwner{public BotComponent Bot=new(); public T? GetComponent<T>() where T:class=>Bot as T;}
}
namespace SAIN.Components
{
 public class BotComponent {public bool IsDead,BotActive=true,GameEnding;public string ProfileId=Guid.NewGuid().ToString("N");public EFT.Player Player=new();public Cover Cover=new();}
 public class Cover{public object? CoverFinder=new();}
}
namespace Comet.Client
{
 internal static class NativeAdapter{internal static bool IsPmcAi(BotComponent b)=>!b.IsDead&&b.Player.IsAI&&b.Player.Profile.Info.Settings.Role is EFT.WildSpawnType.pmcUSEC or EFT.WildSpawnType.pmcBEAR;}
 internal class Resettable {internal void Release(string r){}internal void Clear(){}internal void Reset(string r){}}
 internal class BotState
 {
  internal BotComponent Owner;internal string Id,ProfileId,Name="Test PMC",ActiveLabel="COMET:Test",Decision="Test";
  internal int RequestedFrame=1;internal bool OwnCommand,Retired;
  internal readonly Resettable Engine=new(),VisibleEngine=new(),Intent=new(),Handoff=new(),Sight=new();
  internal object? Probe,SelectedCover,SightPath;
  internal static bool FailCreation;
  internal BotState(BotComponent b,CometConfig c,string session,string? id=null){if(FailCreation)throw new Exception("injected fresh-state failure");Owner=b;Id=id??Guid.NewGuid().ToString("N");ProfileId=b.ProfileId;}
 }
 internal class Pending {internal BotComponent Bot=null!,Leader=null!;}
 internal class Board {internal void Release(string key){}}
 public sealed partial class Plugin
 {
  partial void CoverScanFailure(BotComponent b,Exception ex,ref bool handled);
  partial void ConfigureCoverRetry(BotComponent b,ref Func<bool>? retry);
  private int CoverScanMode(BotComponent b)=>1;
  internal static Plugin? Instance;
  internal CometConfig? config=new();internal string session="c09-test";internal long sequence;
  internal readonly Dictionary<BotComponent,BotState> states=new();
  private readonly Dictionary<BotComponent,object> motionWatches=new();
  private readonly List<Pending> doorSwings=new(),closingPending=new();
  private readonly Dictionary<BotComponent,Pending> closingSelected=new();
  private readonly System.Runtime.CompilerServices.ConditionalWeakTable<BotComponent,object> closingLogs=new();
  private readonly Board crossfireClaims=new(),coverMateBoard=new();
  private readonly Dictionary<string,Pending> supportOperations=new();
  private Diagnostics? diagnostics;
  internal bool Authority=true,InGhost,FailPending;
  private bool Ghost(string id)=>InGhost;
  internal TestLogger Logger=new();internal readonly List<string> CleanupLog=new();internal string FailCleanup="";
  internal Plugin(){Instance=this;}
  internal BotComponent Add(EFT.Player? player=null){var b=new BotComponent();if(player!=null)b.Player=player;states[b]=new(b,config!,session);return b;}
  internal void Sweep(){SampleLifecycles();ResetFaultedBots();}
  internal void NextRaid(){ClearLifecycles();faultIsolation.Clear();states.Clear();}
  internal bool Disabled(BotComponent b)=>faultIsolation.BotDisabled(b);
  internal void StopCoverScan(object finder){if(FailPending)throw new Exception("pending iterator failed");CleanupLog.Add("CoverScan");}
  private void EndSupportOperation(Pending x,string reason){}
  private void ClearDoorMotion(){} private void ClearDoorClosing(){} private void EndCoverScans(){}
  private void ClearBlasts(){} private void ClearDoorSupports(string r){} private void ClearMovementHearing(bool all){}
 }
 internal class TestLogger {internal readonly List<string> Messages=new();internal void LogWarning(string text)=>Messages.Add(text);}
}
