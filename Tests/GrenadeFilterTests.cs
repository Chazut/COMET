using Comet.Core;
internal static class GrenadeFilterTests
{
 public static int Run()
 {
  int n=0;void C(bool ok,string why){n++;if(!ok)throw new Exception("G07: "+why);}
  var c=new GrenadeFilterConfig();C(c.Enabled&&c.AllowFlash,"defaults");
  var old=Newtonsoft.Json.JsonConvert.DeserializeObject<CometConfig>("{}")!;old.Validate();C(old.GrenadeFilter.Enabled&&old.GrenadeFilter.AllowFlash,"old config");
  old.GrenadeFilter.AllowFlash=false;var copy=Newtonsoft.Json.JsonConvert.DeserializeObject<CometConfig>(Newtonsoft.Json.JsonConvert.SerializeObject(old))!;C(!copy.GrenadeFilter.AllowFlash,"roundtrip");
  foreach(var pair in new[]{("frag_grenade","fragmentation"),("flash_grenade","flash"),("smoke_grenade","smoke"),("gas_grenade","gas"),("mod_special","unknown")})
  {bool flash=pair.Item1=="flash_grenade";C(GrenadeFilterRules.Classify(pair.Item1,false,0,flash?0:7,flash?20:0,0,flash?0:70)==pair.Item2,"type "+pair.Item1);}
  C(GrenadeFilterRules.Classify("smoke_grenade",true,120,0,0,0,0)=="smoke","native dummy smoke");
  C(GrenadeFilterRules.Classify("frag_grenade",true,0,0,0,0,0)=="dummy","dummy");
  C(GrenadeFilterRules.Classify("frag_grenade",false,30,7,0,0,70)=="special","persistent modded effect");
  // Manimal 2.0.1 RDA canisters inherit flash_grenade from their M18 base but emit for 30s.
  foreach(bool dummy in new[]{false,true}){
   string kind=GrenadeFilterRules.Classify("flash_grenade",dummy,30,0,20,0,0);
   C(kind!="flash"&&GrenadeFilterRules.Reject(kind,true).Length>0,"RDA gas cannot inherit flash permission");
  }
  C(GrenadeFilterRules.Classify("flash_grenade",false,0,7,20,0,70)=="unknown","mixed ambiguous flash");
  foreach(double invalid in new[]{double.NaN,double.PositiveInfinity,-1d})C(GrenadeFilterRules.Classify("frag_grenade",false,0,invalid,0,0,70)=="unknown","invalid property");
  foreach(string kind in new[]{"smoke","gas","special","unknown","dummy"})C(GrenadeFilterRules.Reject(kind,true).Length>0,"excluded "+kind);
  C(GrenadeFilterRules.Reject("fragmentation",false)==""&&GrenadeFilterRules.Reject("flash",false)=="filter-flash-disabled","flash toggle");
  C(GrenadeFilterRules.FlashReject(1.5,-1,.5,.75)=="","native short flash fuse allowed");
  C(GrenadeFilterRules.FlashReject(1.5,-1,1,.75)=="insufficient-arrival-margin","flash flight safety");
  C(GrenadeFilterRules.FlashReject(2,0,.5,.75)=="contact-explosion","flash contact excluded");
  C(GrenadeFilterRules.FlashReject(double.NaN,-1,.5,.75).Length>0,"unknown fuse");
  Console.WriteLine($"G07 policy: {n} checks passed.");return n;
 }
}
