using Comet.Client;
using EFT;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using UnityEngine;
using Newtonsoft.Json.Linq;
int n=0;void C(bool ok,string why){n++;if(!ok)throw new Exception("H03 adapter: "+why);}
var events=new List<string>();
(Plugin p,BotComponent b) Setup(){Time.time=Time.realtimeSinceStartup=10;return(new(),new());}
void Step(Plugin p,BotComponent b,float dt=.11f){Time.time+=dt;Time.realtimeSinceStartup=Time.time;p.Guard(b);}
void Capture(Plugin p)=>events.AddRange(p.diagnostics!.Lines);
foreach(string kind in new[]{"aid","surgery","stims","hands-only"}){
 var(p,b)=Setup();var m=b.BotOwner.Medecine;var lean=b.Mover.Lean;
 if(kind=="aid")m.FirstAid.Using=true;if(kind=="surgery")m.SurgicalKit.Using=true;if(kind=="stims")m.Stimulators.Using=true;
 b.Player.HandsController=new MedicineHands();lean.Held=true;lean.LeanDirection=LeanSetting.Right;lean.LeanAngleValue.LastSmoothedValue=4;
 lean.ManualUpdate();C(lean.SelectionCalls==0&&lean.SmoothCalls==1,"only selection skipped, native smoothing runs");
 C(!lean.Held&&lean.LeanDirection==LeanSetting.None&&lean.LeanAngleValue.LastSmoothedValue==2,"held lean retracted smoothly");
 C(p.states[b].MedicalPosture.Policy.Blocking,"guard registered without existing contact/H01");
 lean.FastLean(LeanSetting.Left);C(lean.LeanDirection==LeanSetting.None,"external cover request cannot lean through guard");
 C(p.Ended.Contains("step")&&p.Ended.Contains("chase")&&p.Ended.Contains("door-step"),"shared tactical suspension");
 C(b.Mover.Stops==0&&b.Mover.Route=="native-cover"&&m.FirstAid.Cancellations+m.SurgicalKit.Cancellations+m.Stimulators.Cancellations==0,"native travel and treatment never cancelled");
 C(b.Player.Pose==.4f&&!b.Player.IsInPronePose,"pose not forced");
 m.FirstAid.Using=m.SurgicalKit.Using=m.Stimulators.Using=false;Step(p,b);
 C(p.Guard(b),"medicine controller remains in hands");
 b.Player.HandsController=new Hands();Step(p,b);C(!p.Guard(b),"source releases immediately when medicine leaves hands");
 b.Player.HandsController=new Player.FirearmController{Ready=false};Step(p,b);C(!p.Guard(b),"no added readiness/centering/outro delay");
 lean.ManualUpdate();C(lean.SelectionCalls==1&&lean.LeanDirection==LeanSetting.Left,"native lean resumes after guard");Capture(p);
}
foreach(string scope in new[]{"disabled","off","scav","player","remote","ghost","inactive","dead","ending","no-world"}){
 var(p,b)=Setup();b.Player.HandsController=new MedicineHands();p.Guard(b);
 switch(scope){case"disabled":p.config!.Enabled=false;break;case"off":p.config!.MedicalPosture.Enabled=false;break;case"scav":b.Pmc=false;break;case"player":b.Player.IsAI=false;break;case"remote":p.Authority=false;break;case"ghost":p.IsGhost=true;break;case"inactive":b.BotActive=false;break;case"dead":b.IsDead=true;break;case"ending":b.GameEnding=true;break;case"no-world":p.world=null;break;}
 int ends=p.Ended.Count;C(!p.Guard(b),scope+" yields");C(!p.states[b].MedicalPosture.Policy.Blocking&&p.Ended.Count==ends,scope+" no mutation");Capture(p);
}
{var(p,b)=Setup();C(!p.Guard(b)&&p.states.Count==0,"idle firearm no observer or log");b.Player.HandsController=new MedicineHands();b.Player.IsInPronePose=true;b.Player.MovementContext.IsGrounded=false;p.Guard(b);C(!p.Ended.Contains("chase")&&!p.Ended.Contains("door-jump"),"airborne route preserved");C(b.Player.IsInPronePose,"prone unchanged");b.Player.MovementContext.IsGrounded=true;Step(p,b);C(p.Ended.Contains("chase"),"grounded tactical route released");Capture(p);}
{var(p,b)=Setup();p.config!.MedicalPosture.Interval=.5;b.BotOwner.Medecine.FirstAid.Using=true;p.Guard(b);b.BotOwner.Medecine.FirstAid.Using=false;b.Player.HandsController=new Player.FirearmController();Step(p,b,.01f);C(p.states[b].MedicalPosture.Policy.Trace!.State=="resumed","flag changes bypass cadence");b.Player.HandsController=new MedicineHands();Step(p,b,.01f);C(p.states[b].MedicalPosture.Policy.Trace!.MedicineHands,"new medicine immediate");p.End(b);C(!p.states[b].MedicalPosture.Policy.Blocking,"teardown clear");Capture(p);}
{var(p,b)=Setup();b.Player.HandsController=new MedicineHands();p.Guard(b);int count=p.diagnostics!.Lines.Count;for(int i=0;i<30;i++)p.Guard(b);C(p.diagnostics.Lines.Count==count,"same tick no log flood");Time.time+=.6f;p.Guard(b);C(p.diagnostics.Lines.Count==count+1,"active heartbeat refreshes RR lease");Capture(p);}
{var(p,b)=Setup();b.SAINLayersActive=false;b.Player.HandsController=new MedicineHands();C(p.Guard(b),"native medicine outside combat layers protected");C(b.Mover.Stops==0&&b.Mover.Route=="native-cover","outside combat no native route takeover");Capture(p);}
foreach(var json in events){var j=JObject.Parse(json);C(j["feature"]!.Value<string>()=="H03"&&j["medicalPosture"]!=null,"real adapter JSON");}
var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
File.WriteAllLines(Path.Combine(root,"artifacts/h03-adapter-events.jsonl"),events);
var fixture=events.First(e=>JObject.Parse(e)["medicalPosture"]!["medicineHands"]!.Value<bool>());
File.WriteAllText(Path.Combine(root,"../spt-raid-review-fork/Tests/fixtures/h03-medical-posture.json"),fixture);
Console.WriteLine($"{n} H03 production adapter/prefix/JSON checks passed");
