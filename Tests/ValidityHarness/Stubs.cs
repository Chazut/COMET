using System.Reflection;
using EFT;
using SAIN.Components.PlayerComponentSpace;

namespace UnityEngine {
 public class Object {
  public bool Destroyed;
  public static bool operator ==(Object? a,Object? b)=>ReferenceEquals(a,b)||(ReferenceEquals(a,null)?b!.Destroyed:ReferenceEquals(b,null)&&a.Destroyed);
  public static bool operator !=(Object? a,Object? b)=>!(a==b);
  public override bool Equals(object? o)=>ReferenceEquals(this,o);
  public override int GetHashCode()=>System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
 }
 public static class Time {public static double unscaledTimeAsDouble;public static float realtimeSinceStartup=10;}
 public struct Vector3 { }
}
namespace EFT {
 public enum WildSpawnType { pmcUSEC,pmcBEAR,assault,bossKilla }
 public interface IPlayer { }
 public class OtherPlayer:IPlayer { }
 public class Profile {public string Nickname="PMC";public Info Info=new();}
 public class Info {public Settings Settings=new();public string? Nickname="Scav",MainProfileNickname;}
 public class Settings {public WildSpawnType Role=WildSpawnType.pmcUSEC;}
 public class Health {public bool Alive=true,Throw;public bool IsAlive=>Throw?throw new Exception("health read"):Alive;}
 public class Transform {public UnityEngine.Object? Original=new();}
 public class Player:UnityEngine.Object,IPlayer {
  public bool IsAI=true,Downed,ThrowDowned;
  public string ProfileId=Guid.NewGuid().ToString();public Profile Profile=new();
  public Transform? Transform=new();public UnityEngine.Object? PlayerBones=new();
  public Health? HealthController=new();public object? MovementContext=new(),CharacterController=new();
  public SAIN.Components.BotComponent? Bot;
  public T? GetComponent<T>() where T:class=>Bot as T;
  public UnityEngine.Vector3 Position=>throw new Exception("must never inspect source position");
 }
 public class BotOwner {public Player GetPlayer=null!;}
 public class BotMemory {public BotOwner _owner=null!;public void SetUnderFire(IPlayer player){} }
}
namespace SAIN.Components {public class BotComponent:UnityEngine.Object {public bool IsDead;}}
namespace SAIN.Classes {public class PlayerMovementController {public void SetTargetMoveDirection(UnityEngine.Vector3 direction,UnityEngine.Vector3 finalMoveDestination,PlayerComponent playerComp,float START_SLOW_DIST=.75f,float STOP_SPRINT_DIST=1){} }}
namespace SAIN.Components.PlayerComponentSpace {
 public class PlayerComponent:UnityEngine.Object {
  public Player Player=new();public SAIN.Components.BotComponent? BotComponent=new();public BotOwner? BotOwner=new();
  public string ProfileId=>Player.ProfileId;
 }
 public class OtherPlayerData {public PlayerComponent OtherPlayerComponent=null!;}
 public class OtherPlayersData {
  public Player Player=new();public Dictionary<string,OtherPlayerData> DataDictionary=new();
  public OtherPlayerData? GetOrAddData(PlayerComponent playerComp)=>null;
  private void PlayerRemoved(string profileId,PlayerComponent playerComp){}
 }
}
namespace HarmonyLib {
 public class HarmonyMethod {public string Name;public Type Type;public int priority;public HarmonyMethod(Type t,string n){Type=t;Name=n;}}
 public static class Priority {public const int First=800;}
 public static class AccessTools {public static MethodInfo? Method(Type t,string n,Type[]? args=null)=>args==null?t.GetMethod(n,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance):t.GetMethod(n,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance,null,args,null);}
 public class Harmony {
  public List<(MethodBase method,HarmonyMethod prefix,HarmonyMethod? postfix)> Patches=new();
  public void Patch(MethodBase m,HarmonyMethod prefix,HarmonyMethod? postfix=null)=>Patches.Add((m,prefix,postfix));
 }
}
namespace Comet.Client {
 public class BotState {public string Id=Guid.NewGuid().ToString();}
 public sealed partial class Plugin {
  public static Plugin? Instance;public bool Authority=true;public Comet.Core.CometConfig? config=new();
  private Diagnostics? diagnostics=null;public string session=Guid.NewGuid().ToString();private int sequence;
  public Dictionary<SAIN.Components.BotComponent,BotState> states=new();public int Warnings;
  public Plugin(){Instance=this;}
  private void WarnFaultSink(Exception e){Warnings++;}
  private static bool ReadDowned(Player p)=>p.ThrowDowned?throw new Exception("Downed read"):p.Downed;
  public static HarmonyLib.Harmony Contracts(){var h=new HarmonyLib.Harmony();PatchValidity(h);return h;}
  public bool Move(PlayerComponent pc)=>ValidityMove(pc);
  public bool Fire(BotMemory m,IPlayer? source)=>ValidityUnderFire(m,source!);
  public bool Remove(OtherPlayersData d,string id,PlayerComponent pc){if(!ValidityRemove(d,id,pc))return false;d.DataDictionary.Remove(id);return true;}
  public bool Add(OtherPlayersData d,PlayerComponent pc,bool succeed=true){OtherPlayerData result=null!;if(!ValidityAdd(d,pc,ref result,out var prior))return false;if(succeed){result=new(){OtherPlayerComponent=pc};d.DataDictionary[pc.ProfileId]=result;}ValidityAdded(d,pc,result,prior);return true;}
  public void Reset()=>ClearValidity();
 }
}
