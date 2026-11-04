using System;
using Comet.Core;

internal static class GrenadeChoiceTests
{
 public static int Run()
 {
  int checks=0;void C(bool v){checks++;if(!v)throw new Exception("G03 policy");}
  var c=new GrenadeChoiceConfig();c.Validate();C(c.Enabled&&c.PreferShortIndoors&&c.ShortFuseDistance==20&&c.MinimumFuse==2.5&&c.ArrivalMargin==.75);
  C(Math.Abs(GrenadeChoiceRules.Flight(20,26,60)!.Value-2)<.00001);
  foreach(var v in new[]{double.NaN,double.PositiveInfinity,0,-1})C(GrenadeChoiceRules.Flight(20,v,25)==null);
  foreach(var v in new[]{double.NaN,0,90,180})C(GrenadeChoiceRules.Flight(20,20,v)==null);
  C(GrenadeChoiceRules.Reject(2.5,-1,false,true,1.75,c)=="");C(GrenadeChoiceRules.Reject(2.5,-1,false,true,1.751,c)=="insufficient-arrival-margin");
  C(GrenadeChoiceRules.Reject(2.49,-1,false,true,1,c)=="fuse-outside-safe-range");C(GrenadeChoiceRules.Reject(5,0,false,true,1,c)=="contact-explosion");
  C(GrenadeChoiceRules.Reject(5,-1,true,true,1,c)=="dummy-item");C(GrenadeChoiceRules.Reject(5,-1,false,false,1,c)=="not-timed-fragmentation");
  var a=new[]{new GrenadeChoiceAlternative{Item="a",Eligible=true,Fuse=3},new GrenadeChoiceAlternative{Item="b",Eligible=true,Fuse=5},new GrenadeChoiceAlternative{Item="z",Eligible=true,Fuse=3}};
  C(GrenadeChoiceRules.Choose(a,true,"z")!.Item=="z");C(GrenadeChoiceRules.Choose(a,true,"")!.Item=="a");C(GrenadeChoiceRules.Choose(a,false,"")!.Item=="b");
  foreach(var action in new Action<GrenadeChoiceConfig>[] {x=>x.MinimumFuse=2.4,x=>x.ArrivalMargin=.4,x=>x.ShortFuseDistance=41,x=>x.MinimumFuse=double.NaN})
  {var x=new GrenadeChoiceConfig();action(x);try{x.Validate();C(false);}catch(ArgumentException){C(true);}}
  var config=new CometConfig();config.GrenadeChoice.ArrivalMargin=1.1;
  var round=Newtonsoft.Json.JsonConvert.DeserializeObject<CometConfig>(Newtonsoft.Json.JsonConvert.SerializeObject(config))!;round.Validate();C(round.GrenadeChoice.ArrivalMargin==1.1);
  C(Newtonsoft.Json.JsonConvert.DeserializeObject<CometConfig>("{}")!.GrenadeChoice.MinimumFuse==2.5);
  Console.WriteLine($"G03 policy: {checks} checks passed.");return checks;
 }
}
