using System;
using System.Diagnostics;
using System.Linq;
using Comet.Core;
using UnityEngine;
using SAIN.Classes.Bot.Search;
using SAIN.SAINComponent.Classes.EnemyClasses;
namespace UnityEngine { public static class Time { public static int frameCount; public static float time; } }
namespace Comet.Core {
 public static class DoorPolicy { public static bool Finite(double n)=>!double.IsNaN(n)&&!double.IsInfinity(n); }
 public sealed class CometConfig { public WeaponSyncConfig WeaponSync=new();public static void Range(double n,double min,double max,string name){} }
}
namespace SAIN.SAINComponent.Classes.EnemyClasses {
 public sealed class Place { public bool HasArrivedPersonal,HasArrivedSquad; }
 public sealed class Places { public Place LastKnownPlace=new(); }
 public sealed class Enemy { public Places KnownPlaces=new(); }
}
namespace SAIN.Classes.Bot.Search {
 public sealed class SearchClass { public Finder PathFinder=new(); }
 public sealed class Finder {
  public Place TargetPlace;public int Checks;public bool Available=true;
  public bool CheckEnemyPath(Enemy e,out string why){Checks++;why="unreachable";if(!Available)return false;TargetPlace=e.KnownPlaces.LastKnownPlace;return true;}
 }
}
namespace Comet.Client {
 public sealed class BotComponent {}
 public sealed class Runtime {
  public WeaponSyncSchedule Schedule=new();public bool Applying,WasInScope,ResourcesDirty=true;
  public float RetryAt;public WeaponSyncTrace Trace;public string[] Contents=new string[3];public object[] Observed=new object[3];
 }
 public sealed class BotState { public Runtime WeaponSync=new(); }
 public sealed class Snapshot { public WeaponSyncFrame Frame;public string[] Contents=new string[3];public object[] Guns=new object[3]; }
 public sealed class Budget { public bool Allowed=true;public bool TryBegin(int f)=>Allowed;public void Charge(double ms){} }
 public partial class Plugin {
  public int Reads,Applies,Records,Ends;public bool Scope=true,Work,ApplySucceeds=true;public string Block="";
  private int weaponSyncFrame=-1;public Budget doorBudget=new();private Stopwatch doorClock=new();private CometConfig config=new();
  private bool WeaponSyncScope(BotComponent b)=>Scope;
  private string SyncBlock(BotComponent b,BotState s)=>Block;
  private void EndWeaponSync(BotState s,string r){Ends++;}
  private void RecordWeaponSync(BotComponent b,BotState s,bool force=false){Records++;}
  private Snapshot ReadWeaponSync(BotComponent b,BotState s){Reads++;return new(){Frame=new(){Safe=true,ResourcesChanged=Work,NativeCurrentMatches=true,SainCurrentMatches=true,
   Slots=new[]{"FirstPrimaryWeapon","SecondPrimaryWeapon","Holster"}.Select(x=>new WeaponSyncSlot{Slot=x,NativeMatches=true,SainMatches=true,SelectorMatches=true}).ToArray()}};}
  private bool ApplyWeaponSync(BotComponent b,BotState s,Snapshot snap,WeaponSyncTrace t){Applies++;return ApplySucceeds;}
 }
 public static class Cases {
  static int checks;static void Check(bool ok,string reason){checks++;if(!ok)throw new Exception(reason);}
  public static void Run(){
   var e=new Enemy();var search=new SearchClass();
   Check(SearchExecutionAdapter.Prepare(search,e,out var reason)&&reason=="search-target-prepared","direct Search prepares SAIN target");
   Check(ReferenceEquals(search.PathFinder.TargetPlace,e.KnownPlaces.LastKnownPlace),"uses known place, not live target");
   for(int i=0;i<100;i++)Check(SearchExecutionAdapter.Prepare(search,e,out reason)&&reason=="search-target-retained","keep native peek/wait state");
   Check(search.PathFinder.Checks==1,"no repeated path reset");
   e.KnownPlaces.LastKnownPlace=new();Check(SearchExecutionAdapter.Prepare(search,e,out reason)&&search.PathFinder.Checks==2,"new known place initializes once");
   e.KnownPlaces.LastKnownPlace.HasArrivedSquad=true;Check(!SearchExecutionAdapter.Prepare(search,e,out reason)&&reason=="search-place-already-visited","visited squad place rejected");
   e.KnownPlaces.LastKnownPlace=new(){HasArrivedPersonal=true};Check(!SearchExecutionAdapter.Prepare(search,e,out reason),"visited personal place rejected");
   e.KnownPlaces.LastKnownPlace=null;Check(!SearchExecutionAdapter.Prepare(search,e,out reason)&&reason=="search-no-known-place","missing contact rejected");
   e.KnownPlaces.LastKnownPlace=new();search.PathFinder.Available=false;var old=search.PathFinder.TargetPlace;
   Check(!SearchExecutionAdapter.Prepare(search,e,out reason)&&reason.StartsWith("search-native-path-refused"),"native refusal propagated");
   Check(ReferenceEquals(search.PathFinder.TargetPlace,old),"failure never invents a target");
   search.PathFinder.Available=true;Check(SearchExecutionAdapter.Prepare(search,e,out reason),"retry can initialize when route recovers");
   var bot=new BotComponent();
   foreach(var block in new[]{"sync-not-initialized","sync-weapon-not-ready","sync-native-operation-pending","sync-medical-operation","sync-hands-busy","sync-held-weapon-between-slots","sync-firing","sync-melee-not-initialized"}){
    Time.time=0;Time.frameCount=0;var p=new Plugin{Block=block};var s=new BotState();p.SampleWeaponSync(bot,s);
    Check(p.Reads==0&&p.Applies==0&&p.Records==1&&s.WeaponSync.Trace.Reason==block,"busy operation avoids equipment scans: "+block);
    Check(s.WeaponSync.Schedule.Dirty&&!s.WeaponSync.Applying,"pending repair retained, scope released");
    Time.time=.1f;Time.frameCount++;p.SampleWeaponSync(bot,s);Check(p.Records==1,"retry cadence preserved");
    Time.time=.3f;Time.frameCount++;p.Block="";p.Work=true;p.SampleWeaponSync(bot,s);
    Check(p.Reads==2&&p.Applies==1&&!s.WeaponSync.Schedule.Dirty&&s.WeaponSync.Trace.Verified,"repair and post-mutation validation after native operation");
   }
   Time.time=0;Time.frameCount=0;var clean=new Plugin();var cleanState=new BotState();clean.SampleWeaponSync(bot,cleanState);
   Check(clean.Reads==1&&clean.Applies==0&&!cleanState.WeaponSync.Schedule.Dirty,"unchanged equipment read only once");
   var failed=new Plugin{Work=true,ApplySucceeds=false};var fs=new BotState();failed.SampleWeaponSync(bot,fs);
   Check(failed.Reads==1&&failed.Applies==1&&fs.WeaponSync.Schedule.Dirty,"failed transaction remains pending");
   var outside=new Plugin{Scope=false};outside.SampleWeaponSync(bot,new());Check(outside.Reads==0&&outside.Ends==1,"out-of-scope cleanup unchanged");
   var budget=new Plugin();budget.doorBudget.Allowed=false;budget.SampleWeaponSync(bot,new());Check(budget.Reads==0,"shared work budget respected");
   Console.WriteLine($"Raid387 native-boundary regression: {checks} checks passed.");
  }
 }
}
