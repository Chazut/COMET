using Comet.Core;
using EFT.Interactive;
using SAIN.Components;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;

namespace UnityEngine {
 public struct Vector3(float x,float y,float z) {
  public float x=x,y=y,z=z;public static Vector3 up=>new(0,1,0);
  public static Vector3 operator +(Vector3 a,Vector3 b)=>new(a.x+b.x,a.y+b.y,a.z+b.z);
  public static Vector3 operator *(Vector3 a,float t)=>new(a.x*t,a.y*t,a.z*t);
 }
 public class GameObject {public bool activeInHierarchy=true;}
 public struct Bounds {public Vector3 min;}
 public class Collider {public Bounds bounds=>new(){min=new(0,Low,0)};public Door? Door;public bool enabled=true,isTrigger;public GameObject gameObject=new();public Vector3 A,B;public float Low=0,High=2;
  public T? GetComponentInParent<T>() where T:class=>Door as T;}
 public enum QueryTriggerInteraction {Ignore}
 public static class Physics {
  public static List<Collider> World=new();public static bool Throw;public static int Calls;
  public static int OverlapCapsuleNonAlloc(Vector3 a,Vector3 b,float radius,Collider[] output,int mask,QueryTriggerInteraction trigger) {
   Calls++;if(Throw)throw new InvalidOperationException("physics unavailable");int count=0;
   foreach(var c in World){if(!c.enabled||!c.gameObject.activeInHierarchy||c.isTrigger||c.High<a.y-radius||c.Low>b.y+radius)continue;
    if(DoorSpatial.SegmentDistance(new(a.x,0,a.z),new(c.A.x,0,c.A.z),new(c.B.x,0,c.B.z))>radius)continue;
    if(count==output.Length)break;output[count++]=c;}
   return count;
  }
 }
 public static class Time {public static float time,realtimeSinceStartup;}
}
namespace EFT { public static class LayersMaskController {public const int PlayerStaticCollisionsMask=1;} }
namespace EFT.Interactive {public enum EDoorState {Open,Shut,Interacting,Locked} public class Door {public string Id="door";public EDoorState DoorState=EDoorState.Open;public Collider? Collider;public List<Collider> CollisionColliders=>Collider==null?new():new(){Collider};public Door(){Collider=new(){Door=this,A=new(0,0,0),B=new(0,0,1)};}}}
namespace SAIN.Components {public class BotComponent { } public class Link {public string name="native",Id="link";public Vector3 Close1=new(-.5f,1,0),Close2_Normal=new(.5f,1,0),Open1=new(-.5f,1,0),Open2=new(-.5f,1,1);public float BottomY;}}
namespace SAIN.SAINComponent.Classes.Mover {public struct DoorDataStruct {public Door Door;public Link Link;}}
namespace Comet.Client {
 internal class DoorWork {public DoorDataStruct Data;}
 internal class BotState {public DoorSpatialTrace? SpatialTrace;public string ProfileId="pmc",Name="Geometry PMC",Id="instance",SpatialKey="";public float SpatialAt=-1000;}
 internal class Sink {public List<string> Lines=new();public void Write(string value)=>Lines.Add(value);}
 internal static class Diagnostics {public static readonly Newtonsoft.Json.JsonSerializerSettings Json=new(){ContractResolver=new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()};}
 internal static class Api {public static SinkBus Decisions=new();internal class SinkBus {public List<string> Lines=new();public void Publish(string json,Action<Exception> error)=>Lines.Add(json);}}
 internal static class Logger {public static void LogWarning(string message)=>throw new Exception(message);}
 public sealed partial class Plugin {
 private bool RecoveryOwnsStall(BotState s)=>false;
  private CometConfig config=new();private Dictionary<BotComponent,BotState> states=new();private Sink diagnostics=new();private string session="c03-harness";private int sequence;private bool subscriberWarned;
  private static DoorPoint DP(Vector3 p)=>new(p.x,p.y,p.z);
  internal void Release(BotComponent b)=>EndDoorSpatial(states[b],"ownership-ended");
  internal List<string> Lines=>diagnostics.Lines;internal CometConfig Config=>config;
  internal BotComponent Add(){var b=new BotComponent();states[b]=new();return b;}
  internal bool Geometry(DoorDataStruct d,out DoorGeometry g)=>TryDoorGeometry(d,out g);
  internal string Step(BotComponent b,Vector3 p)=>CombatDoorPanel(b,p);
  internal string Check(BotComponent b,DoorDataStruct d,Vector3 point){Time.realtimeSinceStartup=Time.time;if(!TryDoorGeometry(d,out var g))return "invalid";return ValidateDoorPanels(b,new(){Data=d},g,point);}
  internal bool BufferEmpty=>spatialPanels.All(x=>x==null);
 }
}
