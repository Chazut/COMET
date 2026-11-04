using Comet.Core;
using SAIN.Components;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;
namespace UnityEngine {
 public static class Time {public static float time,realtimeSinceStartup;public static int frameCount;}
 public struct Vector3(float x,float y,float z) {
  public float x=x,y=y,z=z;public float sqrMagnitude=>x*x+y*y+z*z;public Vector3 normalized {get{float n=(float)Math.Sqrt(sqrMagnitude);return n>0?new(x/n,y/n,z/n):default;}}
  public static Vector3 up=>new(0,1,0);
  public static Vector3 operator +(Vector3 a,Vector3 b)=>new(a.x+b.x,a.y+b.y,a.z+b.z);
  public static Vector3 operator -(Vector3 a,Vector3 b)=>new(a.x-b.x,a.y-b.y,a.z-b.z);
  public static Vector3 operator *(Vector3 a,float b)=>new(a.x*b,a.y*b,a.z*b);
  public static float Distance(Vector3 a,Vector3 b)=>(float)Math.Sqrt((a-b).sqrMagnitude);
  public static Vector3 Cross(Vector3 a,Vector3 b)=>new(a.y*b.z-a.z*b.y,a.z*b.x-a.x*b.z,a.x*b.y-a.y*b.x);
  public static float Angle(Vector3 a,Vector3 b){a=a.normalized;b=b.normalized;return (float)(Math.Acos(Math.Clamp(a.x*b.x+a.y*b.y+a.z*b.z,-1,1))*180/Math.PI);}
 }
 public enum QueryTriggerInteraction {Ignore}
 public class Collider {public int Id=7;public int GetInstanceID()=>Id;}
 public struct RaycastHit {public Collider? collider;public Vector3 point,normal;}
 // Controlled physics responses, not a Unity geometry simulation.
 public static class Physics {
  public static int Calls;public static bool Blocked,Open,Throw;public static Collider Surface=new();
  public static bool Linecast(Vector3 a,Vector3 b,int mask,QueryTriggerInteraction q){Calls++;if(Throw)throw new Exception("test physics failure");return Blocked;}
  public static bool Linecast(Vector3 a,Vector3 b,out RaycastHit hit,int mask,QueryTriggerInteraction q){Calls++;hit=new(){collider=Surface,point=b,normal=new(0,0,-1)};return !Open;}
 }
}
namespace EFT {public static class LayersMaskController {public const int HighPolyWithTerrainMask=1;}}
namespace SAIN.SAINComponent.Classes.EnemyClasses {
 // No live target Position/Transform in this boundary: hidden coordinates are unavailable.
 public class Enemy {public string EnemyProfileId="target";public bool IsVisible,Valid=true;public float TimeSinceLastKnownUpdated;public Places KnownPlaces=new();}
 public class Place {public Vector3 Position=new(10,0,10);}
 public class Places {public Place LastSeenPlace=new();public Place LastKnownPlace;public Places(){LastKnownPlace=LastSeenPlace;}}
}
namespace SAIN.Components {
 public class BotComponent {public bool Pmc=true,IsDead,BotActive=true,GameEnding;public Vector3 Position;public Player Player=new();public PlayerComponent PlayerComponent=new();public Enemy? GoalEnemy=new();public Enemies EnemyController=new();public BotComponent(){EnemyController.KnownEnemies.Add(GoalEnemy!);}}
 public class Enemies {public List<Enemy> KnownEnemies=new();}
 public class Player {public bool IsAI=true;public Bones PlayerBones=new();}
 public class Bones {public Head Head=new();}public class Head {public Vector3 position=new(0,1,0);}
 public class PlayerComponent {public Character CharacterController=new();}public class Character {public Vector3 CurrentControlLookDirection=new(10,0,10);}
}
namespace Comet.Client {
 internal class BotState {public EncounterRuntime Encounter=new();public string ProfileId="observer",Name="Test PMC",Id="native-observer",Decision="";public List<Candidate> Candidates=new();}
 internal static class NativeAdapter {public static bool IsPmcAi(BotComponent b)=>b.Pmc&&b.Player.IsAI;}
 internal static class Diagnostics {public static Newtonsoft.Json.JsonSerializerSettings Json=new(){ContractResolver=new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()};}
 internal class Sink {public List<string> Lines=new();public void Write(string line)=>Lines.Add(line);}
 internal class Log {public List<string> Warnings=new();public void LogWarning(string line)=>Warnings.Add(line);}
 internal static class Api {public static class Decisions {public static List<string> Lines=new();public static void Publish(string line,Action<Exception> failure)=>Lines.Add(line);}}
 public sealed partial class Plugin {
  internal CometConfig? config=new();internal bool Authority=true,IsGhost,subscriberWarned;internal string session="e03-harness";internal long sequence;
  internal Sink? diagnostics=new();internal Log Logger=new();internal readonly DoorWorkBudget doorBudget=new();
  private bool Ghost(string id)=>IsGhost;private bool ThreatUsable(BotComponent b,Enemy e)=>e.Valid&&b.EnemyController.KnownEnemies.Contains(e);
  private static DoorPoint DP(Vector3 p)=>new(p.x,p.y,p.z);
  internal void Tick(BotComponent b,BotState s)=>SampleEncounter(b,s);
  internal EncounterTrace? Read(BotComponent b,BotState s,Situation situation)=>ReadEncounter(b,b.GoalEnemy!,s,situation);
  internal void Choice(BotState s,Situation situation)=>RecordEncounterChoice(s,situation);
  internal void End(BotState s)=>EndEncounter(s,"encounter-raid-ended");
 }
}
