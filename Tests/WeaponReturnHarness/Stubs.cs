using Comet.Core;
using SAIN.Components;
namespace UnityEngine {public static class Time {public static float time;}}
namespace EFT.InventoryLogic {public enum EquipmentSlot {FirstPrimaryWeapon,SecondPrimaryWeapon,Holster}}
namespace EFT
{
 public class Player
 {
  public object? HandsController=new FirearmController();
  public class FirearmController
  {
   public bool Busy,Reload,CanReload=true;
   public bool IsInInteraction()=>Busy;
   public bool IsInReloadOperation()=>Reload;
   public bool CanStartReload()=>CanReload;
  }
 }
 public class Owner {public string ProfileId="pmc";public Manager? WeaponManager=new();}
 public class Manager {public bool IsReady=true;public BotWeaponSelector? Selector;}
 public class BotWeaponSelector
 {
  public Owner? _owner;public bool IsWeaponReady=true,IsChanging,Accept=true;
  public int Requests;public Action? OnRequest;
  public bool TryChangeToSlot(InventoryLogic.EquipmentSlot slot,bool main){Requests++;OnRequest?.Invoke();return Accept;}
 }
}
namespace SAIN.Components
{
 public class BotComponent
 {
  public bool Pmc=true,BotActive=true,IsDead,GameEnding,SAINLayersActive=true;
  public string ProfileId="pmc";
  public EFT.Player Player=new();public EFT.Owner? BotOwner=new();
 }
}
namespace SAIN
{
 public static class SAINEnableClass
 {
  public static BotComponent? Bot;
  public static bool GetSAIN(string id,out BotComponent bot){bot=Bot!;return bot!=null;}
 }
}
namespace Comet.Client
{
 internal static class NativeAdapter {internal static bool IsSupportedAi(BotComponent b)=>b.Pmc;}
 internal sealed class WeaponReturnRuntime {internal readonly WeaponReturnPolicy Policy=new();internal bool Fault;internal string Intent="";}
 internal sealed class BotState
 {
  internal readonly WeaponReturnRuntime WeaponReturn=new();
  internal BotState(BotComponent b,CometConfig c,string session){}
 }
 public sealed partial class Plugin
 {
  internal static Plugin? Instance;
  internal CometConfig? config=new();internal string session="raid";
  internal readonly Dictionary<BotComponent,BotState> states=new();
  internal bool Authority=true,IsGhost;private bool Ghost(string id)=>IsGhost;
  internal WeaponReturnFrame? Frame;internal int Observations,Records,Errors;
  internal Plugin(){Instance=this;}
  private WeaponReturnConfig RangeArbiterConfig()=>config!.WeaponReturn;
  private WeaponReturnFrame? WeaponReturnFrameFor(BotComponent b,BotState s)=>Frame;
  private void ObserveWeaponReturn(BotComponent b,BotState s)
  {
   Observations++;
   if(Frame!=null)s.WeaponReturn.Policy.Observe(Frame.Now,Frame.Current.Id,Frame.Current.Slot,Frame.Ready,config!.WeaponReturn);
  }
  private void RecordWeaponReturn(BotComponent b,BotState s)=>Records++;
  private void WeaponReturnError(BotState s,Exception ex){Errors++;}
  internal bool Request(EFT.BotWeaponSelector selector,out bool result){result=false;return ChooseWeaponReturn(selector,ref result);}
  internal bool Ready(BotComponent b,EFT.Player.FirearmController? firearm)=>WeaponHandsReady(b,firearm);
 }
}
