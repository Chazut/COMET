using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
internal static class ManimalContractTests
{
 public static int Run(){const string path="C:/Games/SPT-4.1/BepInEx/plugins/CSGas/CSGasClient.dll";
  if(!File.Exists(path)){Console.WriteLine("G08 optional installed-provider contract: skipped (mod absent).");return 0;}
  int n=0;void C(bool v,string why){n++;if(!v)throw new Exception("G08 installed provider: "+why);}
  using var stream=File.OpenRead(path);using var pe=new PEReader(stream);var md=pe.GetMetadataReader();
  var types=md.TypeDefinitions.Select(x=>md.GetTypeDefinition(x)).Where(t=>!t.IsNested).ToDictionary(t=>md.GetString(t.Namespace)+"."+md.GetString(t.Name));
  foreach(var entry in new[]{("Manimal.CSGas.Gas.CsGasCloud",new[]{"_exploded","_emitter","_smoke"},new[]{"Start","GetProtection"}),
   ("Manimal.CSGas.Gas.VolumetricGasEmitter",new[]{"_origin","_finished","_emitUntil","_particleLifetime"},new[]{"Create","IsInside"}),
   ("Manimal.CSGas.Plugin",new[]{"GasPointRadiusM","GasRequiresLineOfSight"},new[]{"Awake"})}){
    C(types.TryGetValue(entry.Item1,out var t),entry.Item1);
    var fields=t.GetFields().Select(x=>md.GetString(md.GetFieldDefinition(x).Name)).ToHashSet();var methods=t.GetMethods().Select(x=>md.GetString(md.GetMethodDefinition(x).Name)).ToHashSet();
    foreach(var f in entry.Item2)C(fields.Contains(f),entry.Item1+"."+f);foreach(var m in entry.Item3)C(methods.Contains(m),entry.Item1+"."+m);
  }
  var sha=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));C(sha=="851589BF528A27CEEF9D079B832892335DDD395091C311880466B93D5775DD71","audited 2.0.0 build identity (re-audit if changed)");
  Console.WriteLine($"G08 installed Manimal binary contract: {n} checks; SHA256 {sha}. No game assembly executed.");return n;
 }
}
