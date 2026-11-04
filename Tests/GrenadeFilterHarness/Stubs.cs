namespace EFT {public class BotGrenadeController {public SAIN.Components.Owner? _owner;public bool DoThrow()=>true;}}
namespace SAIN {public static class SAINEnableClass {internal static SAIN.Components.BotComponent? Bot;public static bool GetSAIN(string id,out SAIN.Components.BotComponent? b){b=Bot?.ProfileId==id?Bot:null;return b!=null;}}}
namespace Comet.Client {public sealed partial class Plugin {
 internal bool NativeThrow(SAIN.Components.BotComponent b){SAIN.SAINEnableClass.Bot=b;bool result=false;return OffensiveThrowGuard(new EFT.BotGrenadeController{_owner=b.BotOwner},ref result);}
 internal bool UnrelatedNativeThrow(){SAIN.SAINEnableClass.Bot=null;bool result=false;return OffensiveThrowGuard(new EFT.BotGrenadeController(),ref result);}
 internal bool FilterPrepare(SAIN.Components.BotComponent b)=>PrepareOffensiveGrenade(b,GrenadeState(b));
 internal void FilterPatch()=>PatchGrenadeFilter(new());
}}
