using Comet.Client;
using Comet.Core;
using EFT;
using EFT.Ballistics;
using EFT.InventoryLogic;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;
using Newtonsoft.Json.Linq;

internal static class H02Tests
{
 internal static void Run()
 {
  int n=0;var events=new List<string>();void C(bool ok,string why){n++;if(!ok)throw new Exception("H02 adapter: "+why);}
  (Plugin p,BotComponent b,Meds item) Setup(){Time.time=10;Time.realtimeSinceStartup=10;var p=new Plugin();p.config!.SurgeryInterruption.Enabled=true;var b=new BotComponent();b.EnemyController.KnownEnemies.Add(new(b));
   b.Player.HealthController.Parts[EBodyPart.LeftLeg].Current=0;b.Player.HealthController.Destroyed.Add(EBodyPart.LeftLeg);
   var item=new Meds{Id="surgery-kit",AllowedPart=EBodyPart.LeftLeg};item.HealthEffectsComponent.Duration=20;item.HealthEffectsComponent.DamageEffects[EDamageEffectType.DestroyedPart]=new();b.Player.InventoryController.Items.Add(item);
   C(p.Choose(b).decision==ESelfActionType.Surgery,"surgery selected");p.Start(b);C(p.states[b].Medical.Interruption.Active,"H02 watches real H01 operation");return(p,b,item);}
  void Capture(Plugin p){events.AddRange(p.diagnostics!.Lines.Where(l=>JObject.Parse(l)["feature"]!.Value<string>()=="H02"));}
  foreach(var danger in new[]{"hit","grenade","visible","known","steps","native"}){
   var(p,b,item)=Setup();var r=p.states[b].Medical;var kit=b.BotOwner.Medecine.SurgicalKit;Time.time=11;
   var e=b.EnemyController.KnownEnemies[0];
   switch(danger){case"hit":b.Medical.TimeSinceShot=0;break;case"grenade":b.Grenade.GrenadeDangerPoint=new();break;case"visible":e.IsVisible=true;e.KnownPlaces.LastSeenPlace=new(){Position=new(35,0,0),TimeSincePositionUpdated=0};break;case"known":e.KnownPlaces.LastSeenPlace=new(){Position=new(18,0,0),TimeSincePositionUpdated=2};break;case"steps":e.KnownPlaces.LastHeardPlace=new(){Position=new(14,0,0),TimeSincePositionUpdated=0};p.Hear(e,SAINSoundType.FootStep,e.KnownPlaces.LastHeardPlace);break;default:kit.CancelCurrent();break;}
   p.Sample(b);C(r.Interruption.Cancelling&&kit.Cancels==1,danger+" one native cancellation");C(!r.Interruption.Trace!.HandsReleased&&!r.Interruption.Trace.WeaponReady,"animation not skipped");
   C(!p.Continue(b)&&!p.Choose(b).result,"stop retaining surgery decision");int stops=b.Mover.Stops;p.SurgeryAction(b);C(b.Mover.Stops==stops,"do not keep stopping mover after cancel");
   for(int i=0;i<10;i++)p.Sample(b);C(kit.Cancels==1,"repeated danger no spam");C(item.MedKitComponent!.HpResource==100&&b.Player.HealthController.IsBodyPartDestroyed(EBodyPart.LeftLeg),"no resource mutation or free limb repair");
   Time.time=12;kit.Finish(false);p.Sample(b);C(r.Interruption.Trace.HandsReleased&&!r.Interruption.Trace.WeaponReady,"native end observed before weapon");C(r.Policy.Trace!.State=="ended","H01 released after actual hands end");
   Time.time=13;b.Player.HandsController=new Player.FirearmController{Ready=false};p.Sample(b);C(r.Interruption.Cancelling,"firearm object alone not ready");
   ((Player.FirearmController)b.Player.HandsController).Ready=true;p.Sample(b);C(r.Interruption.Trace.WeaponReady&&!r.Interruption.Active,"ready firearm observed");C(item.MedKitComponent.HpResource==99,"native consumption not refunded");
   e.IsVisible=false;e.KnownPlaces.LastSeenPlace=new();e.KnownPlaces.LastHeardPlace=null;b.Grenade.GrenadeDangerPoint=null;r.Steps.Clear();
   Time.time=18.99f;C(!p.Choose(b).result,"surgery blocked until retry");Time.time=20;C(p.Choose(b).decision==ESelfActionType.Surgery,"retry plus H01 safety permits new surgery");Capture(p);
  }
  foreach(var kind in new[]{"stale","far","mixed","unheard","oldstep"}){
   var(p,b,_)=Setup();Time.time=11;var e=b.EnemyController.KnownEnemies[0];
   if(kind=="stale"||kind=="mixed")e.KnownPlaces.LastSeenPlace=new(){Position=new(5,0,0),TimeSincePositionUpdated=10};
   if(kind=="far"||kind=="mixed")b.EnemyController.KnownEnemies.Add(new(b){KnownPlaces=new(){LastSeenPlace=new(){Position=new(80,0,0),TimeSincePositionUpdated=0}}});
   if(kind=="unheard")p.Hear(e,SAINSoundType.FootStep,new(){Position=new(1,0,0)});
   if(kind=="oldstep")p.states[b].Medical.Steps.Enqueue((5,new(1,0,0)));
   p.Sample(b);C(!p.states[b].Medical.Interruption.Cancelling&&b.BotOwner.Medecine.SurgicalKit.Cancels==0,kind+" cannot interrupt");Capture(p);
  }
  foreach(var mode in new[]{"off","h01-off","ghost","not-pmc","authority","not-sain","dead","no-world"}){
   var(p,b,_)=Setup();switch(mode){case"off":p.config!.SurgeryInterruption.Enabled=false;break;case"h01-off":p.config!.MedicalChoice.Enabled=false;break;case"ghost":p.IsGhost=true;break;case"not-pmc":b.Pmc=false;break;case"authority":p.Authority=false;break;case"not-sain":b.SAINLayersActive=false;break;case"dead":b.IsDead=true;break;default:p.world=null;break;}
   b.Grenade.GrenadeDangerPoint=new();p.Sample(b);C(!p.states[b].Medical.Interruption.Active&&b.BotOwner.Medecine.SurgicalKit.Cancels==0,mode+" native untouched");Capture(p);
  }
  foreach(var bhf in new[]{false,true}){
   var(p,b,_)=Setup();var kit=b.BotOwner.Medecine.SurgicalKit;kit.Bhf=bhf;kit.Finish();b.Grenade.GrenadeDangerPoint=new();p.Sample(b);C(kit.Cancels==0&&p.states[b].Medical.Interruption.Trace!.Completed,"completion race native/BHF");Capture(p);
  }
  {var(p,b,_)=Setup();Time.time=11;b.Grenade.GrenadeDangerPoint=new();p.Sample(b);p.End(b);C(!p.states[b].Medical.Interruption.Active,"raid teardown clears observer");b.BotOwner.Medecine.SurgicalKit.Finish();C(!p.states[b].Medical.Callback,"late callback invalidated");Capture(p);}
  {var(p,b,_)=Setup();Time.time=11;b.Grenade.GrenadeDangerPoint=new();p.Sample(b);b.BotOwner.Medecine.SurgicalKit.Finish(false);b.Player.HandsController=new Player.FirearmController();Time.time=12;p.Sample(b);Time.time=16;b.Grenade.GrenadeDangerPoint=null;
   b.Player.HealthController.Parts[EBodyPart.Chest].Current=15;var aid=new MedKit{Id="aid",AllowedPart=EBodyPart.Chest};b.Player.InventoryController.Items.Add(aid);C(p.Choose(b).decision==ESelfActionType.FirstAid,"surgery backoff does not block urgent first aid");}
  {var(p,b,_)=Setup();p.config!.SurgeryInterruption.Interval=.5;Time.time=10.01f;p.Tick(b);Time.time=10.02f;b.Medical.TimeSinceShot=0;p.Tick(b);C(b.BotOwner.Medecine.SurgicalKit.Cancels==1,"hit bypasses polling cadence");}
  {var(p,b,_)=Setup();p.config!.SurgeryInterruption.Interval=.5;Time.time=10.01f;p.Tick(b);Time.time=10.02f;b.Grenade.GrenadeDangerPoint=new();p.Tick(b);C(b.BotOwner.Medecine.SurgicalKit.Cancels==1,"grenade bypasses polling cadence");}
  {var(p,b,_)=Setup();b.BotOwner.Medecine.SurgicalKit.CurUsingMeds=new Meds{Id="other"};b.Grenade.GrenadeDangerPoint=new();p.Sample(b);C(b.BotOwner.Medecine.SurgicalKit.Cancels==0&&!p.states[b].Medical.Interruption.Active,"another operation is never cancelled");}
  {var(p,b,_)=Setup();var kit=b.BotOwner.Medecine.SurgicalKit;Time.time=11;b.Grenade.GrenadeDangerPoint=new();p.Sample(b);b.Player.HandsController=new Player.FirearmController();p.Sample(b);C(p.states[b].Medical.Interruption.Cancelling&&!p.states[b].Medical.Interruption.Trace!.WeaponReady,"pending native callback prevents false weapon-ready");kit.Finish(false);kit.CurUsingMeds=new Meds{Id="next-kit"};b.Player.HandsController=new Player.FirearmController();Time.time=12;p.Sample(b);C(p.states[b].Medical.Interruption.Trace!.WeaponReady,"RefreshMeds changing kit on completion preserves return tracking");}
  string root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));File.WriteAllLines(Path.Combine(root,"artifacts/h02-adapter-events.jsonl"),events);
  File.WriteAllText(Path.Combine(root,"artifacts/h02-surgery-interruption.json"),events.Select(JObject.Parse).First(e=>e["surgeryInterruption"]!["state"]!.Value<string>()=="cancel-requested"&&e["surgeryInterruption"]!["evidence"]?.Type==JTokenType.Object).ToString());
  Console.WriteLine($"H02 production adapter: {n} checks passed; {events.Count} actual JSON events");
 }
}
