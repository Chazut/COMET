using Comet.Core;
using EFT;
using EFT.InventoryLogic;
using SAIN.Components;
using SAIN.SAINComponent.Classes.WeaponFunction;
using HarmonyLib;
using UnityEngine;
using System.Runtime.CompilerServices;
namespace UnityEngine {
 public static class Time {public static float time=10,realtimeSinceStartup=10;}
 public struct Vector3(float x,float y,float z){public float x=x,y=y,z=z;public float magnitude=>(float)Math.Sqrt(x*x+y*y+z*z);public static float Distance(Vector3 a,Vector3 b)=>new Vector3(a.x-b.x,a.y-b.y,a.z-b.z).magnitude;}
}
namespace EFT.InventoryLogic {
 public class Item {public string Id=Guid.NewGuid().ToString();public LightComponent? Light;public T? GetItemComponent<T>() where T:class=>Light as T;}
 public class Weapon:Item {public List<Item> Mods=new();}
 public class LightComponent {public Item Item;public bool IsActive=true;public int SelectedMode=2;public LightComponent(){Item=new(){Light=this};}}
}
namespace EFT {
 public struct LightsState {public string Id;public bool IsActive;public int LightMode;}
 public class Player {public bool IsAI=true,IsSprintEnabled;public Vector3 Velocity;public object? HandsController;
  public class FirearmController {public Weapon Item=new();public bool Ready=true,Remove,Reload,Spawn,Inventory,Interact,Processing,Refuse,Throw;public List<LightsState[]> Commands=new();
   public bool IsInRemoveOperation()=>Remove;public bool IsInReloadOperation()=>Reload;public bool IsInSpawnOperation()=>Spawn;public bool IsInventoryOpen()=>Inventory;public bool IsInInteractionStrictCheck()=>Interact;public bool IsHandsProcessing()=>Processing;
   public virtual bool SetLightsState(LightsState[] values, bool force=false, bool animated=true){Commands.Add(values);if(force||!animated)throw new Exception("unexpected native flags");if(Throw)throw new Exception("native command failure");if(Refuse)return false;foreach(var v in values)foreach(var i in Item.Mods)if(i.Id==v.Id&&i.Light!=null){i.Light.IsActive=v.IsActive;i.Light.SelectedMode=v.LightMode;}return true;}
  }
 }
 public class BotOwner {public WeaponManager WeaponManager=new();public Door DoorOpener=new();public Medicine Medecine=new();public Shoot ShootData=new();public Night NightVision=new();}
 public class WeaponManager {public Weapon? CurrentWeapon;public Reload Reload=new();public Grenades Grenades=new();}public class Reload {public bool Reloading;}public class Grenades {public bool ThrowindNow;}
 public class Door {public bool Interacting;}public class Medicine {public bool Using;}public class Shoot {public bool Shooting;}public class Night {public bool UsingNow;}
}
namespace SAIN.Preset.Shared.Enums {public enum ECombatDecision{Freeze,StandAndShoot,Search,Retreat} public enum ESelfActionType{None,Reload,FirstAid}}
namespace SAIN.SAINComponent.Classes.EnemyClasses {
 public class Enemy {public bool EnemyKnown=true,IsVisible;public string EnemyProfileId="enemy";public Places KnownPlaces=new();public float RealDistance=500;}
 public class Places {public Place? LastSeenPlace,LastHeardPlace=new();public Place? LastSquadPlace;}
 public class Place {public Vector3 Position=new(20,0,0);public float Timestamp=10;public float TimeSincePositionUpdated=>Time.time-Timestamp;}
}
namespace SAIN.Components {
 public class BotComponent {public string ProfileId=Guid.NewGuid().ToString();public bool Pmc=true,BotActive=true,IsDead,GameEnding,SAINLayersActive=true;
  public Player Player=new();public BotOwner BotOwner=new();public SAIN.SAINComponent.Classes.EnemyClasses.Enemy? GoalEnemy=new();public Vector3 Position;
  public object? CurrentAction=new SAIN.Layers.Combat.Solo.FreezeAction();public Decision Decision=new();public Door DoorOpener=new();public Grenade Grenade=new();public Flash Flashed=new();public Mover Mover=new();public Shoot ManualShoot=new();public Cover Cover=new();public Enemies EnemyController=new();
  public bool Disposed;[MethodImpl(MethodImplOptions.NoInlining)]public void Dispose(){Disposed=true;}
 }
 public class Decision {public SAIN.Preset.Shared.Enums.ECombatDecision CurrentCombatDecision;public SAIN.Preset.Shared.Enums.ESelfActionType CurrentSelfDecision;}
 public class Grenade {public object? GrenadeDangerPoint;}public class Flash{public bool IsFlashed;}public class Mover{public bool Running;}public class Cover{public object? CoverInUse;}
 public class Enemies {public List<SAIN.SAINComponent.Classes.EnemyClasses.Enemy> KnownEnemies=new();}
}
namespace SAIN.Layers.Combat.Solo {public class FreezeAction{} public class StandAndShootAction{} public class SearchAction{}}
namespace SAIN.SAINComponent.Classes.WeaponFunction {
 public class BotLightController(BotComponent bot) {public BotComponent Bot=bot;public bool Want=true;public int NativeCalls;public float Next;public Action? Before;public Action? After;
  [MethodImpl(MethodImplOptions.NoInlining)]private void updateLightToggle(){NativeCalls++;var hand=Bot.Player.HandsController as Player.FirearmController;var d=hand?.Item.Mods.FirstOrDefault()?.Light;if(d!=null&&d.IsActive!=Want&&Time.time>Next){Next=Time.time+1;hand!.SetLightsState(new[]{new LightsState{Id=d.Item.Id,LightMode=d.SelectedMode,IsActive=Want}});}}
  [MethodImpl(MethodImplOptions.NoInlining)]public void Tick(){Before?.Invoke();updateLightToggle();After?.Invoke();}
 }
}
namespace Comet.Client {
 internal class PolicyFlag {public bool Active;}internal class RuntimeFlag {public PolicyFlag Policy=new();}
 internal class BotState {public LightDisciplineRuntime LightDiscipline=new();public RuntimeFlag Ambush=new(),HoldAngle=new();public string ProfileId,Name="Test PMC",Id="light-instance";public BotState(BotComponent b,CometConfig c,string session){ProfileId=b.ProfileId;}}
 internal static class NativeAdapter {public static bool IsPmcAi(BotComponent b)=>b.Pmc&&b.Player.IsAI;}
 internal static class Diagnostics {public static Newtonsoft.Json.JsonSerializerSettings Json=new(){ContractResolver=new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()};}
 internal class Sink {public List<string> Lines=new();public void Write(string line)=>Lines.Add(line);}
 internal class Log {public List<string> Warnings=new();public void LogWarning(string s)=>Warnings.Add(s);}
 internal static class Api {public static class Decisions {public static void Publish(string json,Action<Exception> failure){}}}
 public sealed partial class Plugin {
  internal static Plugin? Instance;internal object? world=new();internal CometConfig? config=new();internal bool Authority=true,IsGhost,subscriberWarned;internal string session="l01-harness";internal long sequence;
  internal Dictionary<BotComponent,BotState> states=new();internal Sink? diagnostics=new();internal Log Logger=new();
  public Plugin(){Instance=this;}internal static void Patch()=>PatchLightDiscipline(new Harmony("comet.l01.harness"));
  private bool Ghost(string id)=>IsGhost;private static bool ValidPoint(Vector3 p)=>float.IsFinite(p.x)&&float.IsFinite(p.y)&&float.IsFinite(p.z);
  private static bool WeaponHandsReady(BotComponent b,Player.FirearmController? f)=>f?.Ready==true;private static DoorPoint DP(Vector3 p)=>new(p.x,p.y,p.z);
  internal void End(BotComponent b)=>EndLightDiscipline(states[b],"light-raid-ended");internal void Watch(BotComponent b)=>SampleLightDiscipline(b,states[b]);
 }
}
