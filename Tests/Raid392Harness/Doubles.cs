global using SAIN;
using Comet.Core;
namespace UnityEngine { public static class Time {public static float time=10;} }
namespace EFT {public class Player {public Movement MovementContext=new();}public class Movement {public List<(float,bool)> Calls=new();public bool Throw;public void SetTilt(float v,bool force){if(Throw)throw new Exception("animation failure");Calls.Add((v,force));}}}
namespace EFT.InventoryLogic {public class Weapon {public enum EReloadMode{OnlyBarrel,Other}public EReloadMode ReloadMode=EReloadMode.OnlyBarrel;public int ChamberAmmoCount=1;public object[] Chambers=new object[2];}}
namespace SAIN.Components {public class BotComponent{public EFT.Player Player=new();public Cover Cover=new();} public class Cover{public object? CoverInUse;}}
namespace SAIN {public enum LeanSetting{None,Left,Right}public class LeanClass {public Components.BotComponent Bot=new();public bool IsHoldingLean;public LeanSetting LeanDirection=LeanSetting.Left;public void FastLean(LeanSetting side)=>LeanDirection=side;}}
namespace Comet.Client {
 internal class CombatLeanRuntime {internal CombatLean Policy=new();internal LeanClass? Owner;internal LeanSetting Applied;internal bool Owns,AnimatorApplied,Fault;}
 internal class BotState {internal CombatLeanRuntime CombatLean=new();}
 internal class Log {internal void LogWarning(string s){} }
 public sealed partial class Plugin {
  private static Plugin? Instance;private CometConfig config=new();private Dictionary<SAIN.Components.BotComponent,BotState> states=new();private bool Authority=true;private Log Logger=new();
  private static EFT.InventoryLogic.Weapon? barrelReloadRequest;
  internal CombatLeanFrame Frame=new(){Combat=new(){Now=10,Target="enemy",Distance=10,Known=new(1,2,3)},GeometryFresh=true,LeftSafe=true,RightSafe=true};
  internal LeanClass Lean=new();internal BotState State=new();
  internal Plugin(){Instance=this;states[Lean.Bot]=State;State.CombatLean.Owner=Lean;State.CombatLean.Applied=LeanSetting.Left;State.CombatLean.Owns=true;
    if(!State.CombatLean.Policy.Begin(Frame,config.CombatLean,0,0,.99,0,0,0))throw new Exception("fixture lean must start");}
  private CombatLeanFrame LeanFrame(SAIN.Components.BotComponent b,BotState s,LeanClass lean)=>Frame;
  private void RecordCombatLean(BotState s,bool force=false){}
  internal void Apply()=>ApplyCombatLeanAnimation(Lean);
  internal void End()=>EndCombatLean(State,"test-ended");
  internal static int Predicate(EFT.InventoryLogic.Weapon w,EFT.InventoryLogic.Weapon? owner){barrelReloadRequest=owner;try{return BarrelReloadChamberPredicate(w);}finally{barrelReloadRequest=null;}}
 }
}
