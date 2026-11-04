using Comet.Core;
using Newtonsoft.Json;

internal static class RestLightsTests
{
 public static int Run()
 {
  int n=0;void C(bool ok,string why){n++;if(!ok)throw new Exception("L03: "+why);}
  var c=new RestLightsConfig();C(c.Enabled&&c.HoldDelay==2&&c.MaxSpeed==.5&&c.EpisodeInterval==10,"defaults");
  foreach(var json in new[]{"{}","{\"RestLights\":null}"}){var old=JsonConvert.DeserializeObject<CometConfig>(json)!;old.Validate();C(old.RestLights.Enabled,"old config migration");}
  foreach(var prop in typeof(RestLightsConfig).GetProperties().Where(x=>x.PropertyType==typeof(double)))
  foreach(var bad in new[]{double.NaN,double.PositiveInfinity,-1d,10000d}){var invalid=new RestLightsConfig();prop.SetValue(invalid,bad);bool thrown=false;try{invalid.Validate();}catch(ArgumentException){thrown=true;}C(thrown,"invalid "+prop.Name);}
  var p=new RestLightsPolicy();C(!p.Update(10,"camp",true,c),"first sample waits");C(!p.Update(11.99,"camp",true,c),"stable delay");C(p.Update(12,"camp",true,c),"boundary");p.Consume(12,c);
  C(!p.Update(200,"camp",true,c),"same pause never retries even after cooldown");p.Release();C(!p.Update(201,"camp",true,c)&&p.Episode==2,"new pause");C(p.Update(203,"camp",true,c),"new pause ready");p.Consume(203,c);
  C(!p.Update(204,"other",true,c),"new owner starts delay");C(!p.Update(206,"other",true,c),"cooldown across episodes");C(p.Update(213,"other",true,c),"cooldown expires");p.Consume(213,c);
  C(!p.Update(1,"other",true,c)&&p.NextEpisode==0,"new raid clock");C(!p.Update(4,"other",false,c)&&p.Context=="","lost eligibility resets pause");
  foreach(string layer in new[]{"unknown","SAIN Combat","QuestingBots",""})C(RestLightsOwner.Behavior(layer,"peaceful","Finished","Camp")=="","unknown owner not assumed resting");
  foreach(string status in new[]{"Moving","Looting","Extracting","Failed","None"})C(RestLightsOwner.Behavior("OrbitBrainLayer","peaceful",status,"Camp")=="","ORBIT active task excluded");
  foreach(string category in new[]{"Exfil","AirdropLoot","MultiStep:power",""})C(RestLightsOwner.Behavior("OrbitBrainLayer","peaceful","Finished",category)=="","ORBIT special task excluded");
  C(RestLightsOwner.Behavior("OrbitBrainLayer","peaceful","Finished","AmbushExtract")=="Orbit:CampingAmbushExtract","camp behavior");C(RestLightsOwner.Behavior("PatrolAssault","peaceLook","","")=="peaceLook","native wait");
  C(RestLightsOwner.Behavior("PatrolAssault","simplePatrol","","")=="","slow patrol is not rest");
  var folder=Path.Combine(Path.GetTempPath(),"comet-l03-"+Guid.NewGuid().ToString("N"));
  try{var store=new Comet.Server.ConfigStore(Path.Combine(folder,"config.json"));var d=store.Read();d.RestLights.Enabled=false;d.RestLights.HoldDelay=3;var saved=store.Save(d,d.Revision);C(saved.Revision==2&&!store.Read().RestLights.Enabled,"persisted toggle");C(JsonConvert.DeserializeObject<CometConfig>(store.ForGame())!.RestLights.HoldDelay==3,"game config");d=store.Read();d.RestLights.MaxSpeed=-1;bool rejected=false;try{store.Save(d,d.Revision);}catch(ArgumentException){rejected=true;}C(rejected&&store.Read().RestLights.MaxSpeed==.5,"invalid save preserves file");}
  finally{if(Directory.Exists(folder))Directory.Delete(folder,true);}
  Console.WriteLine($"{n} L03 policy/config checks passed");return n;
 }
}
