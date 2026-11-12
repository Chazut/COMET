using System;
using System.Linq;
using Comet.Core;

// Behavioral contract: zzap fbb86b83 (see SOURCE_BASELINE.md).
internal static class VisibleDecisionTests
{
 public static int Run()
 {
  int n=0; void Check(bool ok,string why){n++;if(!ok)throw new Exception("VisibleDecision: "+why);}

 VisibleSituation Frame()=>new(){Contact=new(){Now=20,Target="enemy",IsAi=true,IsSupportedBot=true,IsAuthority=true,IsActive=true,Visible=true,HasContact=true,KnownDistance=10,Health=1,Ammo=1},WeaponReady=true,CanShoot=true,PushReachable=true,PushPathLength=10,CoverDistance=2};
 CometConfig Only(VisibleTactic a){var c=new CometConfig();c.TacticalMistakes.Enabled=false;c.VisibleCombat.ShootWeight=a==VisibleTactic.Shoot?1:0;c.VisibleCombat.CoverWeight=a==VisibleTactic.Cover?1:0;c.VisibleCombat.PushWeight=a==VisibleTactic.Push?1:0;return c;}
 foreach(var a in Enum.GetValues<VisibleTactic>()){var e=new VisibleEnemyDecision(Only(a));var f=Frame();Check(e.Evaluate(f).Decision==a,"source candidate can execute: "+a);}
 foreach(Action<VisibleSituation> change in new Action<VisibleSituation>[]{v=>v.ProbeReady=false,v=>v.ThreatCount=20,v=>v.ThreatsTruncated=true,v=>v.PushRouteRisk=1,v=>v.CoverRouteRisk=1,v=>v.PushPathLength=100,v=>v.Contact.Hurt=true,v=>v.Contact.LowAmmo=true}){
  var f=Frame();change(f);Check(new VisibleEnemyDecision(Only(VisibleTactic.Push)).Evaluate(f).Decision==VisibleTactic.Push,"removed extra route/threat/status veto");}
 var frame=Frame();frame.PushReachable=false;Check(new VisibleEnemyDecision(Only(VisibleTactic.Push)).Evaluate(frame).Decision==null,"source requires rush capability");
 frame=Frame();frame.Contact.KnownDistance=25;Check(new VisibleEnemyDecision(Only(VisibleTactic.Push)).Evaluate(frame).Decision==null,"source visible distance limit");
 var policy=new VisibleEnemyDecision(Only(VisibleTactic.Shoot));frame=Frame();policy.Evaluate(frame);frame.Contact.Now+=.1;frame.CanShoot=false;Check(policy.Evaluate(frame).Decision==VisibleTactic.Shoot,"source ranked memory survives temporary no-shot");
 frame.Contact.Now+=4;Check(policy.Evaluate(frame).Decision==null,"no-shot eventually yields to native decisions");
 foreach(Action<VisibleSituation> change in new Action<VisibleSituation>[]{v=>v.Contact.Ghost=true,v=>v.Contact.IsAuthority=false,v=>v.Contact.IsSupportedBot=false,v=>v.Contact.Visible=false}){frame=Frame();change(frame);Check(new VisibleEnemyDecision(new()).Evaluate(frame).Decision==null,"ownership boundary preserved");}

  return n;
 }
}
