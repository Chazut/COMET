global using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
AssemblyLoadContext.Default.Resolving+=(_,name)=>{
 var path=Path.Combine("C:/Games/SPT-4.1/EscapeFromTarkov_Data/Managed",name.Name+".dll");
 return File.Exists(path)?AssemblyLoadContext.Default.LoadFromAssemblyPath(path):null;
};
Comet.Client.Plugin.Test();

// The method extractor shares these unused imports with other adapter harnesses.
namespace Comet.Core { internal class ImportMarker {} }
namespace SAIN { internal class ImportMarker {} }
namespace SAIN.Components { internal class ImportMarker {} }
namespace Comet.Client {
 public sealed partial class Plugin {
  private static readonly Dictionary<Type,Func<object,object?>> faultOwners=new();
  private static Func<object,object?> FaultOwnerAccessor(Type type){faultOwners[type]=_=>null;return faultOwners[type];}
  // Real game parameter types, with no game methods/constructors invoked.
  private void Reload(BotReload reload){}
  private void Bullets(BotWeaponManager manager){}
  private void Selection(BotWeaponSelector selector){}
  private void Player(EFT.Player player){}
  private void Other(object value){}
  [MethodImpl(MethodImplOptions.NoInlining)]
  public static void Test(){
   PrepareFaultOwners();
   foreach(var type in new[]{typeof(BotReload),typeof(BotWeaponManager),typeof(BotWeaponSelector),typeof(EFT.Player)})
    if(!faultOwners.ContainsKey(type))throw new Exception("Native accessor missing: "+type.Name);
   if(faultOwners.ContainsKey(typeof(object)))throw new Exception("Unrelated type warmed");
   Console.WriteLine("Owner warmup: 5 production checks passed against real native types; no game callbacks executed.");
  }
 }
}
