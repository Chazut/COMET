using Comet.Core;
using EFT;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
using System.Runtime.CompilerServices;
namespace UnityEngine { public static class Time { public static float time=10,realtimeSinceStartup=10; } }
namespace Comet.Core {
 public static class MedicalChoice { public static bool Finite(double x)=>!double.IsNaN(x)&&!double.IsInfinity(x); }
 public class CometConfig { public bool Enabled=true;public long Revision=3;public MedicalPostureConfig MedicalPosture=new();public static void Range(double v,double min,double max,string name){if(!MedicalChoice.Finite(v)||v<min||v>max)throw new ArgumentException(name);} }
}
namespace EFT {
 public interface IMedsController{}
 public class Item { public string Id="med-kit"; }
 public class Hands { public Item? Item; }
 public class MedicineHands:Hands,IMedsController {public MedicineHands(){Item=new();}}
 public class Player { public bool IsAI=true,IsInPronePose;public int Shots,Moves;public float Pose=.4f;
  public Hands? HandsController=new FirearmController();public Movement MovementContext=new();
  public class FirearmController:Hands {public bool Ready=true;}
 }
 public class Movement {public bool IsGrounded=true;}
}
namespace SAIN.Preset.Shared.Enums { public enum LeanSetting{None,Left,Right} }
namespace SAIN.Components {
 public class Med {public bool Using;public int Cancellations;}
 public class Medicine {public Med FirstAid=new(),Stimulators=new(),SurgicalKit=new();public bool Using=>FirstAid.Using||Stimulators.Using||SurgicalKit.Using;}
 public class Owner {public Medicine Medecine=new();}
 public class Mover {public LeanClass Lean;public Mover(BotComponent b){Lean=new(b);}public int Stops;public string Route="native-cover";}
 public class BotComponent {public bool Pmc=true,BotActive=true,IsDead,GameEnding,SAINLayersActive=true;public string ProfileId=Guid.NewGuid().ToString();public Player Player=new();public Owner BotOwner=new();public Mover Mover;public BotComponent(){Mover=new(this);} }
}
namespace SAIN.SAINComponent.Classes.Mover {
 public class Smooth {public float LastSmoothedValue;}
 public class LeanClass(BotComponent b){public BotComponent Bot=b;public LeanSetting LeanDirection;public bool Held;public int SelectionCalls,SmoothCalls;public Smooth LeanAngleValue=new();
  [MethodImpl(MethodImplOptions.NoInlining)]public void FastLean(LeanSetting value){Comet.Client.Plugin.LeanRequest(this,ref value);LeanDirection=value;}
  public void ResetHoldLean(){Held=false;}
  [MethodImpl(MethodImplOptions.NoInlining)]private void UpdateLeanSetting(float time){SelectionCalls++;FastLean(LeanSetting.Left);}
  [MethodImpl(MethodImplOptions.NoInlining)]public void ManualUpdate(){if(Comet.Client.Plugin.LeanSelection(this))UpdateLeanSetting(Time.time);SmoothCalls++;LeanAngleValue.LastSmoothedValue*=.5f;}
 }
}
namespace Comet.Client {
 internal static class NativeAdapter {public static bool IsPmcAi(BotComponent b)=>b.Pmc&&b.Player.IsAI&&!b.IsDead;}
 internal class BotState {internal MedicalPostureRuntime MedicalPosture=new();public string ProfileId,Name="COMET Test PMC",Id="test-instance";internal BotState(BotComponent b,CometConfig c,string session){ProfileId=b.ProfileId;}}
 public class Diagnostics { internal static string Serialize(object value)=>Newtonsoft.Json.JsonConvert.SerializeObject(value,Json);public static Newtonsoft.Json.JsonSerializerSettings Json=new(){ContractResolver=new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()};public List<string> Lines=new();public void Write(string x)=>Lines.Add(x);}
 public static class Api {public static class Decisions {public static List<string> Lines=new();public static void Publish(string json,Action<Exception> onError)=>Lines.Add(json);}}
 public class Log {public List<string> Lines=new();public void LogWarning(string s)=>Lines.Add(s);}
 public sealed partial class Plugin {
  public static Plugin? Instance;internal CometConfig? config=new();public object? world=new();public bool Authority=true,IsGhost;public Log Logger=new();
  internal Dictionary<BotComponent,BotState> states=new();internal string session="h03-test";internal long sequence;internal bool subscriberWarned;public Diagnostics? diagnostics=new();
  public List<string> Ended=new();public Plugin(){Instance=this;}
  private bool Ghost(string id)=>IsGhost;
  private static bool WeaponHandsReady(BotComponent b,Player.FirearmController? f)=>f?.Ready==true;
  public static bool LeanSelection(LeanClass lean)=>MedicalLeanSelection(lean);
  public static void LeanRequest(LeanClass lean,ref LeanSetting side)=>MedicalLeanRequest(lean,ref side);
  public bool Guard(BotComponent b)=>MedicalPostureGuard(b);
  public void End(BotComponent b)=>EndMedicalPosture(states[b],"medical-posture-raid-ended");
  private void EndCombatLean(BotState s,string why)=>Ended.Add("lean");private void EndAngle(BotState s,string why)=>Ended.Add("angle");
  private void EndHoldAngle(BotState s,string why)=>Ended.Add("hold");private void EndWeave(BotState s,string why)=>Ended.Add("weave");
  private void EndCombatStep(BotState s,string why)=>Ended.Add("step");private void EndCornerPeek(BotState s,string why)=>Ended.Add("corner");
  private void EndChase(BotState s,string why)=>Ended.Add("chase");private void EndDoorStep(BotState s,string why)=>Ended.Add("door-step");
  private void EndDoorRun(BotState s,string why)=>Ended.Add("door-run");private void EndDoorJump(BotState s,string why)=>Ended.Add("door-jump");private void EndFeint(BotState s,string why)=>Ended.Add("feint");
 }
}
