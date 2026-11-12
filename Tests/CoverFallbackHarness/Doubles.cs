using Comet.Core;
using SAIN.Components;
using SAIN.SAINComponent.SubComponents.CoverFinder;
using UnityEngine;
using UnityEngine.AI;
namespace UnityEngine {
 public struct Vector3(float x,float y,float z){public float x=x,y=y,z=z;public static float Dot(Vector3 a,Vector3 b)=>a.x*b.x+a.y*b.y+a.z*b.z;public static Vector3 up=>new(0,1,0);public static Vector3 operator +(Vector3 a,Vector3 b)=>new(a.x+b.x,a.y+b.y,a.z+b.z);public static Vector3 operator -(Vector3 a,Vector3 b)=>new(a.x-b.x,a.y-b.y,a.z-b.z);public static Vector3 operator *(Vector3 a,float b)=>new(a.x*b,a.y*b,a.z*b);public float sqrMagnitude=>x*x+y*y+z*z;public float magnitude=>(float)Math.Sqrt(sqrMagnitude);public Vector3 normalized=>magnitude<.0001?new():this*(1/magnitude);public static float Distance(Vector3 a,Vector3 b)=>(a-b).magnitude;public static float Angle(Vector3 a,Vector3 b)=>(float)(Math.Acos(Math.Clamp((a.x*b.x+a.y*b.y+a.z*b.z)/(a.magnitude*b.magnitude),-1,1))*180/Math.PI);}
 public static class Time {public static float time=10,realtimeSinceStartup=10;public static int frameCount;}
 public enum QueryTriggerInteraction {Ignore}
 public struct Bounds {public Vector3 center,extents;}
 public class Collider {public object? attachedRigidbody;public Bounds bounds=new(){center=new(2,1,2),extents=new(.5f,1,.5f)};}
 public static class Physics {
  public static Collider[] Obstacles=[];public static int Overlaps,Rays;
  public static Func<Vector3,Vector3,bool> Cast=(a,b)=>true;
  public static int OverlapSphereNonAlloc(Vector3 p,float radius,Collider[] buffer,int mask,QueryTriggerInteraction q){Overlaps++;int n=Math.Min(buffer.Length,Obstacles.Length);Array.Copy(Obstacles,buffer,n);return n;}
  public static bool Linecast(Vector3 a,Vector3 b,int mask,QueryTriggerInteraction q){Rays++;return Cast(a,b);}
 }
}
namespace UnityEngine.AI {
 public enum NavMeshPathStatus {PathComplete,PathPartial}
 public class NavMeshPath {public NavMeshPathStatus status;public Vector3[] corners=[];}
 public struct NavMeshHit {public Vector3 position;}
 public static class NavMesh {
  public const int AllAreas=-1;public static int Queries;public static bool Valid=true;
  public static Func<Vector3,Vector3> Map=p=>p;
  public static bool SamplePosition(Vector3 p,out NavMeshHit hit,float d,int area){Queries++;hit=new(){position=Map(p)};return Valid;}
 }
}
public static class LayersMaskController {public const int HighPolyWithTerrainMask=1;}
namespace SAIN.Components {
 public class BotComponent {
  public bool Pmc=true,BotActive=true,IsDead,GameEnding;public string ProfileId=Guid.NewGuid().ToString();
  public Vector3 Position;public Player Player=new();public Enemy? GoalEnemy=new();public Cover Cover=new();public Squad Squad=new();public Controller EnemyController=new();
 }
 public class Player {public bool IsAI=true;public Profile Profile=new();} public class Profile {public string Nickname="test PMC";}
 public class Enemy {public bool EnemyKnown=true,IsVisible;public string EnemyProfileId="target";public float TimeSinceLastKnownUpdated;public Places KnownPlaces=new();}
 public class Places {public Vector3? LastKnownPosition=new(0,0,12);}
 public class Cover {public CoverPoint? CoverInUse,CoverPoint_MovingTo;}
 public class Squad {public Dictionary<string,BotComponent> Members=new();} public class Controller {public List<Enemy> KnownEnemies=new();}
}
namespace SAIN.SAINComponent.SubComponents.CoverFinder {
 public class PathData {
  public static NavMeshPathStatus Status;public static int Queries;public static Func<Vector3,Vector3,Vector3[]> Route=(a,b)=>[a,b];
  public NavMeshPath Path=new();public float PathLength,TimeLastUpdated;
  public void CalcPath(Vector3 a,Vector3 b){Queries++;TimeLastUpdated=Time.time;Path.status=Status;Path.corners=Route(a,b);PathLength=0;for(int i=1;i<Path.corners.Length;i++)PathLength+=Vector3.Distance(Path.corners[i-1],Path.corners[i]);}
 }
 public class Data {public bool IsBad;}
 public class CoverPoint(BotComponent b,Collider c,PathData path,Vector3 p) {public BotComponent Bot=b;public Vector3 Position=p;public Collider? Collider=c;public PathData PathData=path;public NavMeshPath PathToPoint=>PathData.Path;public bool Spotted;public Data CoverData=new();}
}
namespace Comet.Api {public static class Decisions {public static List<string> Events=new();public static void Publish(string s,Action<Exception> fail)=>Events.Add(s);}}
namespace Comet.Client {
 internal static class NativeAdapter {public static bool IsSupportedAi(BotComponent b)=>b.Pmc&&b.Player.IsAI;}
 internal static class ContactSafety {public static bool Allowed=true;public static bool AllowsGoal(BotComponent b)=>Allowed;}
 internal static class ProgressAdapter {public static string CoverKey(CoverPoint p)=>Destination(p.Position);public static string Destination(Vector3 p)=>$"{p.x}:{p.y}:{p.z}";}
 internal static class VisibleAdapter {public static bool ValidPath(NavMeshPath p,Vector3 a,Vector3 b)=>p.status==NavMeshPathStatus.PathComplete&&p.corners.Length>=2&&Vector3.Distance(a,p.corners[0])<=4&&Vector3.Distance(b,p.corners[^1])<=2;}
 internal class Sink {public void Write(string s){}}
 internal static class Diagnostics {public static string Serialize(object o)=>Newtonsoft.Json.JsonConvert.SerializeObject(o,new Newtonsoft.Json.JsonSerializerSettings{ContractResolver=new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()});}
 internal class BotState {public string Id="instance";public PlanProgress Progress=new(new());}
 internal class Budget {public bool Available=true;private int frame=-1,used;public bool TryBegin(int f,object? owner=null){if(f!=frame){frame=f;used=0;}if(!Available||used>=2)return false;used++;return true;}public void Charge(double ms){}}
 public sealed partial class Plugin {
  internal static Plugin? Instance;internal CometConfig config=new();internal bool Authority=true,IsGhost;internal string session="fallback-test";internal long sequence;
  internal Sink? diagnostics=new();internal Dictionary<BotComponent,BotState> states=new();internal Budget doorBudget=new();private System.Diagnostics.Stopwatch doorClock=new();
  private bool Ghost(string id)=>IsGhost;public Plugin(){Instance=this;}
  partial void TickCoverFallbacks();partial void ClearCoverFallbacks();partial void CheckFallbackCover(BotComponent b,CoverPoint point,ref bool handled,ref bool usable,ref string reason);
  internal void Tick()=>TickCoverFallbacks();internal void Clear()=>ClearCoverFallbacks();internal int Count=>coverFallbacks.Count;
  internal CoverFallbackRuntime R(BotComponent b)=>coverFallbacks[b];
 }
}
