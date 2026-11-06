using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using Comet.Core;
using SAIN.Components;
using SAIN.Components.CoverFinder;
using SAIN.SAINComponent.SubComponents.CoverFinder;
using UnityEngine;
namespace UnityEngine {
 public static class Time {public static double timeAsDouble;public static int frameCount;public static float realtimeSinceStartup=>(float)timeAsDouble;}
 public class WaitForSeconds(float seconds){}
 public struct Vector3(float x,float y,float z){public float x=x,y=y,z=z;public Vector3 normalized=>this;public static Vector3 operator -(Vector3 a,Vector3 b)=>new(a.x-b.x,a.y-b.y,a.z-b.z);}
}
namespace HarmonyLib {
 public class Harmony {public void Patch(MethodInfo m,HarmonyMethod? prefix=null,HarmonyMethod? postfix=null,HarmonyMethod? transpiler=null){if(m==null)throw new Exception("missing patch");}}
 public class HarmonyMethod(Type t,string n){}
 public class CodeInstruction(OpCode code,object operand){public OpCode opcode=code;public object operand=operand;public bool Calls(MethodInfo m)=>Equals(operand,m);}
 public static class AccessTools {
  const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
  public static MethodInfo Method(Type t,string n)=>t.GetMethod(n,Flags)!;
  public static MethodInfo PropertyGetter(Type t,string n)=>t.GetProperty(n,Flags)!.GetGetMethod(true)!;
  public static MethodInfo EnumeratorMoveNext(MethodInfo m)=>m;
 }
}
namespace SAIN.Models.Enums {public enum ECoverFinderStatus {None,SearchingColliders}}
namespace SAIN.Components {
 public class BotComponent {
  public bool Pmc=true,BotActive=true,IsDead,GameEnding;
  public string ProfileId="pmc1";public Player Player=new();public Enemy? GoalEnemy=new();public Vector3 NavMeshPosition;
  public Cover Cover=new();
 }
 public class Player {public Profile Profile=new();}public class Profile {public string Nickname="PMC 1";}
 public class Enemy {public string EnemyProfileId="known-enemy";public Vector3? LastKnownPosition=new Vector3(10,0,0);}
 public class Cover {public CoverFinderComponent CoverFinder=null!;}
}
namespace SAIN.SAINComponent.SubComponents.CoverFinder {
 public class CoverPoint {public string Id="cover-1";public bool Spotted;public object? Collider=new();public Data CoverData=new();public bool Pass=true;}
 public class Data {public bool IsBad;}
 public class CoverAnalyzer {public int Calls;public bool Throws;public bool RecheckCoverPoint(CoverPoint p,Vector3 a,Vector3 b,Vector3 c,out string why){Calls++;if(Throws)throw new Exception("geometry unavailable");why=p.Pass?"ok":"path-blocked";return p.Pass;}}
}
namespace SAIN.Components.CoverFinder {
 public class CoverFinderComponent {
  public BotComponent Bot;public List<CoverPoint> CoverPoints {get;}=new();public SAIN.Models.Enums.ECoverFinderStatus CurrentStatus;
  private CoverAnalyzer CoverAnalyzer {get;}=new();public CoverAnalyzer Analyzer=>CoverAnalyzer;
  public int Steps=3,Iterations,Factories;public bool Throw,ChangeContact;
  public CoverFinderComponent(BotComponent b){Bot=b;b.Cover.CoverFinder=this;}
  private IEnumerator FindCoverLoop(){Factories++;while(true){CurrentStatus=SAIN.Models.Enums.ECoverFinderStatus.SearchingColliders;
   // Mimics the six redirected accesses. Geometry is supplied by doubles; the wrapper
   // and transpiler under test are the actual production sources.
   Comet.Client.Plugin.ScanCoverPoints(this).Clear();
   for(int i=0;i<Steps;i++){Iterations++;if(Throw)throw new Exception("scan failure");Comet.Client.Plugin.ScanCoverPoints(this).Add(new(){Id="cover-"+i});if(ChangeContact)Bot.GoalEnemy!.EnemyProfileId="changed";yield return null;}
   CurrentStatus=SAIN.Models.Enums.ECoverFinderStatus.None;yield return new WaitForSeconds(.1f);
  }}
  public IEnumerator Original()=>FindCoverLoop();public void StopLooking(){}
 }
}
namespace SAIN.SAINComponent.Classes.Mover {public class SAINMoverClass(BotComponent b){public BotComponent Bot=b;public bool GoToCoverPoint(CoverPoint point)=>true;}}
namespace Comet.Api {public static class Decisions {public static readonly List<string> Events=new();public static void Publish(string j,Action<Exception> e)=>Events.Add(j);}}
namespace Comet.Client {
 internal static class NativeAdapter {public static bool IsPmcAi(BotComponent b)=>b.Pmc&&!b.IsDead;}
 internal static class ProgressAdapter {public static string CoverKey(CoverPoint? p)=>p?.Id??"";}
 internal class Diagnostics { internal static string Serialize(object value)=>Newtonsoft.Json.JsonConvert.SerializeObject(value,Json);public static readonly Newtonsoft.Json.JsonSerializerSettings Json=new(){ContractResolver=new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()};public void Write(string s){}}
 internal class Log {public List<string> Warnings=new();public void LogWarning(string s)=>Warnings.Add(s);}
 internal class BotState {public string Id="bot-instance";}
 public sealed partial class Plugin {
  internal static Plugin? Instance;internal CometConfig config=new();internal bool Authority=true,IsGhost;
  internal bool Ghost(string id)=>IsGhost;private readonly Log Logger=new();private string session="c07-test";private long sequence;private bool subscriberWarned;private Diagnostics? diagnostics;
  private readonly Dictionary<BotComponent,BotState> states=new();
  public Plugin(){Instance=this;}
  public IEnumerator Wrap(CoverFinderComponent f){IEnumerator e=f.Original();WrapCoverScan(f,ref e);return e;}
  public bool Validate(BotComponent b,CoverPoint point){bool result=false;return ValidateScanCover(new(b),point,ref result);}
  public void Sweep()=>CheckCoverScans();public void End()=>EndCoverScans();public void Stop(CoverFinderComponent f)=>StopCoverScan(f);
  public int Count=>coverScans.Count;public int Warnings=>Logger.Warnings.Count;public void Patches()=>PatchCoverScan(new());
 }
}
