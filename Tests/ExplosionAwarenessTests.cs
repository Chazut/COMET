using Comet.Core;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection.Emit;
internal static class ExplosionAwarenessTests
{
 public static int Run()
 {
  int n=0; void C(bool value,string why){n++;if(!value)throw new Exception("G10: "+why);}
  var c=new ExplosionAwarenessConfig(); C(c.Enabled&&c.NearDistance==25&&c.LookDuration==3&&c.LookInterval==8,"defaults");
  foreach(var value in new[]{double.NaN,double.PositiveInfinity,-1,101}) {c=new(){NearDistance=value};try{c.Validate();C(false,"invalid distance accepted");}catch(ArgumentException){n++;}}
  foreach(var value in new[]{double.NaN,.4,9}) {c=new(){LookDuration=value};try{c.Validate();C(false,"invalid duration accepted");}catch(ArgumentException){n++;}}
  foreach(var value in new[]{double.NaN,2,61}) {c=new(){LookInterval=value};try{c.Validate();C(false,"invalid interval accepted");}catch(ArgumentException){n++;}}
  try{new ExplosionAwarenessConfig{LookDuration=8,LookInterval=7}.Validate();C(false,"overlapping interval accepted");}catch(ArgumentException){n++;}
  var config=Newtonsoft.Json.JsonConvert.DeserializeObject<CometConfig>("{}")!;config.Validate();C(config.ExplosionAwareness.Enabled,"old config default");
  config.ExplosionAwareness=new(){Enabled=false,NearDistance=32,LookDuration=4,LookInterval=12};
  var copy=Newtonsoft.Json.JsonConvert.DeserializeObject<CometConfig>(Newtonsoft.Json.JsonConvert.SerializeObject(config))!;copy.Validate();C(!copy.ExplosionAwareness.Enabled&&copy.ExplosionAwareness.NearDistance==32&&copy.ExplosionAwareness.LookDuration==4&&copy.ExplosionAwareness.LookInterval==12,"all settings roundtrip");
  config.ExplosionAwareness=null!;try{config.Validate();C(false,"null accepted");}catch(ArgumentException){n++;}
  var p=new ExplosionAwareness();c=new();
  ExplosionTrace T()=>new(){Audible=true,Distance=10,Signal="one"};
  C(p.Receive(T(),10,"",c).Remaining==3,"three second look");
  C(p.Tick(11,"",true)!.ValidFor==1,"one second applied lease");
  C(p.Receive(T(),12,"",c).Reason=="explosion-attention-cooldown","no restart");
  C(p.Tick(13,"",false)!.State=="ended","original deadline");
  C(p.Receive(T(),17,"",c).State=="heard","cooldown persists");C(p.Receive(T(),18,"",c).State=="look","interval expires");
  C(p.Tick(19,"combat",false)!.Reason=="combat"&&p.Look==null,"priority aborts");
  string path=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../references/sain-4.5.1/SAIN.dll"));
  using var pe=new PEReader(File.OpenRead(path));var md=pe.GetMetadataReader();
  foreach(var (type,name,count) in new[]{("GrenadeController","GrenadeExplosion",6),("GrenadeController","GrenadeThrown",4),("GrenadeController","RegisterGrenadeExplosionForSAINBots",4),("HearingInput","CheckAddSoundToCache",2),("SAINSteeringClass","TickPlayerSteering",0),("Enemy","EnemyPositionReported",3)})
  {
   var t=md.TypeDefinitions.Select(h=>md.GetTypeDefinition(h)).Single(t=>md.GetString(t.Name)==type);
   var methods=t.GetMethods().Select(h=>md.GetMethodDefinition(h)).Where(m=>md.GetString(m.Name)==name).ToArray();
   C(methods.Length==1,"unambiguous official hook "+name);
   C(methods[0].GetParameters().Select(h=>md.GetParameter(h)).Count(p=>p.SequenceNumber>0)==count,"official parameters "+name);
  }
  var grenadeType=md.TypeDefinitions.Select(h=>md.GetTypeDefinition(h)).Single(t=>md.GetString(t.Name)=="GrenadeController");
  var report=grenadeType.GetMethods().Select(h=>md.GetMethodDefinition(h)).Single(m=>md.GetString(m.Name)=="RegisterGrenadeExplosionForSAINBots");
  var calls=FloorHearingTests.ReadIl(pe.GetMethodBody(report.RelativeVirtualAddress).GetILBytes()!).Where(x=>x.op==OpCodes.Call||x.op==OpCodes.Callvirt).Select(x=>FloorHearingTests.Name(md,(int)x.operand!)).ToArray();
  C(calls.Count(x=>x.EndsWith("GetEnemy"))==1,"actual official explosion IL has exactly one enemy lookup");
  C(calls.Any(x=>x.EndsWith("get_EnemyPosition"))&&calls.Any(x=>x.EndsWith("SetHeard")),"official report really uses live position and contact update");
  Console.WriteLine($"G10 policy/config/native metadata: {n} checks passed.");return n;
 }
}
