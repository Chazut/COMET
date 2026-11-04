using Comet.Core;
using System.Text.Json;
internal static class PostCombatTests
{
 public static int Run()
 {
  int n=0;void C(bool ok,string why){n++;if(!ok)throw new Exception("H04: "+why);}
  var c=new PostCombatConfig();c.Validate();
  C(c.Enabled&&c.Interval==.25&&c.ReloadBelow==70&&c.WatchMin==3&&c.WatchMax==6&&c.CoverRadius==25&&c.RegroupDistance==25&&c.AdvanceMin==4&&c.AdvanceMax==40&&c.Duration==14&&c.MedicalDuration==60,"approved defaults");
  foreach(var prop in typeof(PostCombatConfig).GetProperties().Where(p=>p.PropertyType==typeof(double)))foreach(double bad in new[]{-1d,double.NaN,double.PositiveInfinity,1000d})
  {var badConfig=new PostCombatConfig();prop.SetValue(badConfig,bad);bool rejected=false;try{badConfig.Validate();}catch(ArgumentException){rejected=true;}C(rejected,prop.Name+" invalid config");}
  foreach(var mutate in new Action<PostCombatConfig>[] {v=>v.WatchMin=7,v=>v.AdvanceMin=20,v=>v.Duration=5,v=>v.MedicalDuration=10,v=>v.AdvanceChances.Remove("Chad"),v=>v.AdvanceChances["Chad"]=101,v=>v.AdvanceChances["Chad"]=double.NaN})
  {var bad=new PostCombatConfig();if(bad.AdvanceMin==4)bad.AdvanceMax=10;mutate(bad);bool rejected=false;try{bad.Validate();}catch(ArgumentException){rejected=true;}C(rejected,"inconsistent config");}
  C(JsonSerializer.Deserialize<CometConfig>("{}")!.PostCombat.ReloadBelow==70,"legacy config defaults");
  var config=new CometConfig();config.PostCombat.ReloadBelow=65;config.Validate();C(JsonSerializer.Deserialize<CometConfig>(JsonSerializer.Serialize(config))!.PostCombat.ReloadBelow==65,"roundtrip");
  PostCombat P(){var p=new PostCombat();p.ObserveFight(9);return p;}
  var f=new PostCombatFrame{Now=10};
  C(!new PostCombat().Tick(f,c,0,0),"spawn or generic patrol is not recovery");
  {var p=P();f.Threat=true;C(!p.Tick(f,c,0,0),"lost vision with known enemy remains combat");f.Threat=false;f.NativePriority=true;C(!p.Tick(f,c,0,0),"native search preempts");f.NativePriority=false;C(p.Tick(f,c,0,0)&&p.Trace!.State=="watch","idle after actual fight starts");p.Applied(true);C(p.Trace!.Applied&&p.Trace.ValidFor==1,"short observable lease");f.Now=12.9;C(p.Tick(f,c,1,1),"watch minimum");f.Now=13;C(!p.Tick(f,c,1,1)&&p.Trace!.Reason=="post-combat-work-complete","early end at 3 seconds");C(!p.Tick(f,c,0,0),"no perpetual restart");p.ObserveFight(14);f.Now=15;C(p.Tick(f,c,0,0)&&p.Trace!.Episode==2,"next real fight rearms");}
  foreach(var chance in c.AdvanceChances)foreach(double health in new[]{1d,.39}){
   var p=P();var x=new PostCombatFrame{Now=10,Personality=chance.Key,CanAdvance=true,Health=health};double expected=chance.Value*(health<.4?.2:1);
   p.Tick(x,c,0,Math.Max(0,expected-.01)/100);C(p.Trace!.AdvanceChance==expected&&p.Trace.AdvanceSelected==(expected>0),"personality and low health weighted chance");
   x.Now=13;p.Tick(x,c,1,1);C(p.Trace.AdvanceSelected==(expected>0),"one roll per episode");
   var boundary=P();boundary.Tick(x,c,0,expected/100);C(!boundary.Trace!.AdvanceSelected,"strict chance boundary including zero");
  }
  foreach(string priority in new[]{"threat","native"}){
   var p=P();var x=new PostCombatFrame{Now=10,NeedsCover=true};p.Tick(x,c,0,0);p.Applied(true);x.Now=10.01;x.Threat=priority=="threat";x.NativePriority=priority=="native";
   C(!p.Tick(x,c,0,0)&&!p.Trace!.Applied,"immediate priority clears lease");x.Threat=x.NativePriority=false;C(!p.Tick(x,c,0,0),"preemption does not reroll");}
  {var p=P();var x=new PostCombatFrame{Now=10,NeedsCover=true,MedicalUseful=true,AmmoPercent=20,Regroup=true,CanAdvance=true};p.Tick(x,c,0,0);C(p.Trace!.State=="cover","cover before nonurgent treatment");p.Complete("cover","unreachable");x.Now=11;p.Tick(x,c,0,0);C(p.Trace.State=="medical","no cover does not loop forever");x.MedicalUseful=false;x.Now=12;p.Tick(x,c,0,0);C(p.Trace.State=="reload","reload real low magazine");p.Complete("reload","no ammo");x.Now=13;p.Tick(x,c,0,0);C(p.Trace.State=="watch","unavailable ammo does not block");x.Now=16;p.Tick(x,c,0,0);C(p.Trace.State=="regroup","optional regroup");p.Complete("regroup","unreachable");x.Now=17;p.Tick(x,c,0,0);C(p.Trace.State=="check","optional known position only");p.Complete("check","done");x.Now=18;C(!p.Tick(x,c,0,0),"all useful work complete before14s");}
  {var p=P();var x=new PostCombatFrame{Now=10,NeedsCover=true,MedicalUseful=true,MedicalUrgent=true};p.Tick(x,c,0,0);C(p.Trace!.State=="medical","urgent bleed before cover");x.Now=24;C(p.Tick(x,c,0,0),"useful medicine can exceed normal budget");x.Now=70;C(!p.Tick(x,c,0,0),"no new medicine after60s");}
  {var p=P();var x=new PostCombatFrame{Now=10,MedicalBusy=true};p.Tick(x,c,0,0);x.Now=90;C(p.Tick(x,c,0,0),"actual animation can finish beyond budget");x.MedicalBusy=false;C(!p.Tick(x,c,0,0),"release after animation beyond budget");}
  {var p=P();var x=new PostCombatFrame{Now=10,MedicalBusy=true};p.Tick(x,c,0,0);x.Now=191;C(!p.Tick(x,c,0,0),"stalled observation eventually yields without cancel");}
  foreach(string mode in new[]{"cover","reload","hands"}){var p=P();var x=new PostCombatFrame{Now=10,NeedsCover=mode=="cover",Reloading=mode=="reload",HandsReady=mode!="hands"};p.Tick(x,c,0,0);x.Now=mode=="cover"?24:71;C(!p.Tick(x,c,0,0),"bounded "+mode);}
  foreach(string mode in new[]{"scope","off","clock","nan","health","ammo"}){var p=P();var x=new PostCombatFrame{Now=10};var cfg=new PostCombatConfig();p.Tick(x,cfg,0,0);x.Now=11;switch(mode){case"scope":x.Scope=false;break;case"off":cfg.Enabled=false;break;case"clock":x.Now=5;break;case"nan":x.Now=double.NaN;break;case"health":x.Health=double.NaN;break;case"ammo":x.AmmoPercent=double.NaN;break;}C(!p.Tick(x,cfg,0,0)&&!p.WantsRecovery(12),"invalid/scope clear "+mode);}
  {var p=P();C(!p.WantsRecovery(70),"old combat does not wake recovery");p.Clear("raid-end");C(!p.WantsRecovery(10)&&p.LastFight==-1000,"raid cleanup");}
  Console.WriteLine($"{n} H04 policy/config checks passed");return n;
 }
}
