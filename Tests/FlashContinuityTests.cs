using Comet.Core;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
internal static class FlashContinuityTests
{
 public static int Run(){int n=0;void C(bool v,string why){n++;if(!v)throw new Exception("G09: "+why);}
  C(new FlashContinuityConfig().Enabled,"default on");C(FlashContinuityRules.End(10,2,15)==15,"never shorten");C(FlashContinuityRules.End(10,8,15)==18,"new duration wins");
  C(FlashContinuityRules.StrongEnd(10,2,.7,14)==14,"strong phase retained");C(FlashContinuityRules.StrongEnd(10,2,.7,9)==11.4,"expired strong phase not recreated for whole old duration");
  var old=Newtonsoft.Json.JsonConvert.DeserializeObject<CometConfig>("{}")!;old.Validate();C(old.FlashContinuity.Enabled,"old config defaults");old.FlashContinuity.Enabled=false;var copy=Newtonsoft.Json.JsonConvert.DeserializeObject<CometConfig>(Newtonsoft.Json.JsonConvert.SerializeObject(old))!;copy.Validate();C(!copy.FlashContinuity.Enabled,"config roundtrip");
  old.FlashContinuity=null!;try{old.Validate();throw new Exception("missing config accepted");}catch(ArgumentException){n++;}
  string path=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../references/sain-4.5.1/SAIN.dll"));
  using var stream=File.OpenRead(path);using var pe=new PEReader(stream);var md=pe.GetMetadataReader();
  var t=md.TypeDefinitions.Select(x=>md.GetTypeDefinition(x)).Single(t=>md.GetString(t.Namespace)=="SAIN.SAINComponent.Classes.Sense"&&md.GetString(t.Name)=="BotFlashedClass");
  var fields=t.GetFields().Select(x=>md.GetString(md.GetFieldDefinition(x).Name)).ToHashSet();foreach(var f in new[]{"_flashEndTime","_blindFireTime","_firstStage","_secondStage"})C(fields.Contains(f),"official field "+f);
  var methods=t.GetMethods().Select(x=>md.GetMethodDefinition(x)).ToArray();foreach(var m in new[]{"ApplyFlash","ApplyModifiers","set_LastSeenEnemyPoint","ManualUpdate","Dispose"})C(methods.Any(x=>md.GetString(x.Name)==m),"official hook "+m);
  C(!methods.Any(x=>md.GetString(x.Name)=="RefreshFlash"),"official SAIN has no zzap RefreshFlash: explicit continuation only");
  Console.WriteLine($"G09 policy/config/native metadata: {n} checks passed.");return n;
 }
}
