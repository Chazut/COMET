using Comet.Core;
using EFT;
using EFT.Interactive;
using SAIN.Components;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
using UnityEngine.AI;
namespace UnityEngine {
 public static class Time {public static float time;public static int frameCount;}
 public class Object {public static object? Found;public static int Finds;public static T? FindObjectOfType<T>() where T:class{Finds++;return Found as T;}}
}
namespace UnityEngine.AI {
 public enum NavMeshPathStatus {PathComplete,PathPartial,PathInvalid}
 public class NavMeshPath {public NavMeshPathStatus status;public Vector3[] corners=Array.Empty<Vector3>();}
}
namespace EFT.Interactive {public class Door {public Vector3 Center;public bool Throws;}}
namespace EFT {public class NavMeshDoorLink {public Door? Door=new();} public class BotDoorsController:UnityEngine.Object {public List<NavMeshDoorLink> _navMeshDoorLinks=new();}}
namespace SAIN.SAINComponent.Classes.Mover {public struct DoorDataStruct(NavMeshDoorLink link){public NavMeshDoorLink Link=link;}}
namespace SAIN.Components {
 public class Owner {public Voxels VoxelesPersonalData=new();}
 public class Voxels {public Voxel? CurVoxel;}
 public class Voxel {public List<NavMeshDoorLink> DoorLinks=new();}
 public class EnemyPath {public NavMeshPath? PathToEnemy;}
}
namespace Comet.Client {
 public sealed class Diagnostics {public List<string> Lines=new();public void Write(string line)=>Lines.Add(line);public static string Serialize(object value)=>System.Text.Json.JsonSerializer.Serialize(value);}
 public class TestLog {public int Warnings;public void LogWarning(string s)=>Warnings++;}
 public sealed partial class Plugin {
  private CometConfig? config=new();private bool Authority=true;private string session="test";private Diagnostics diagnostics=new();public TestLog Logger=new();
  private static Vector3 VP(DoorPoint p)=>new((float)p.X,(float)p.Y,(float)p.Z);
  private static DoorPoint DP(Vector3 p)=>new(p.x,p.y,p.z);
  private static bool TryDoorGeometry(DoorDataStruct data,out DoorGeometry geometry){if(data.Link.Door!.Throws)throw new Exception("destroyed geometry");geometry=new(){Center=DP(data.Link.Door.Center)};return true;}
  public void Index(bool authority=true){Authority=authority;UpdateLocalDoors();}
  public void ResetIndex()=>ClearLocalDoors();
  public NavMeshDoorLink[] Doors(BotComponent bot)=>NearbyDoors(bot).ToArray();
  public static DoorPoint[] Bearing(BotComponent bot,Vector3 known)=>RememberedDoorRoute(bot,known);
  public int Indexed=>localDoorCursor;
  public int Buckets=>localDoors.Count;
  public bool NoDoorReferences=>localDoorSource==null&&localDoors.Count==0;
  public string Summary(){FlushTacticalProbes(true);return diagnostics.Lines.Last();}
 }
}
