using Comet.Client;
using Comet.Core;
using EFT;
using SAIN;
using SAIN.Components;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
using UnityEngine.AI;
internal static class SquadStormHarness
{
 public static void Run()
 {
  int n=0;var events=new List<string>();void C(bool ok,string why){n++;if(!ok)throw new Exception("S06 adapter: "+why);}
  void Step(float dt=.016f){Time.time+=dt;Time.realtimeSinceStartup=Time.time;Time.frameCount++;}
  (Plugin p,BotComponent[] bots,CrossfireAction[] actions) Setup(int count=2)
  {
   Time.time=Time.realtimeSinceStartup=10;Time.frameCount=0;Physics.Blocked=false;Physics.Cast=null;Physics.Ray=null;NavMesh.Valid=true;NavMesh.Sample=null;NavMesh.Route=null;Api.Decisions.Labels.Clear();SAINEnableClass.Bots.Clear();Logger.Warnings.Clear();
   var p=new Plugin();p.config!.SquadStorm.Enabled=true;p.config.SquadTrade.Enabled=p.config.CoverMate.Enabled=p.config.Crossfire.Enabled=false;
   var bots=Enumerable.Range(0,count).Select(i=>new BotComponent{Position=new((i-(count-1)*.5f)*6,0,0)}).ToArray();var members=bots.ToDictionary(b=>b.ProfileId);
   foreach(var b in bots){b.Squad.Members=members;b.EnemyController.KnownEnemies.Add(b.GoalEnemy!);SAINEnableClass.Bots[b.ProfileId]=b;
    p.StormEvidenceByBot[b.ProfileId]=new(){Source=b.ProfileId,At=10,Until=15,Score=.7,Details=new[]{"seen torso: no protection","seen hands: pistol"}};}
   return(p,bots,bots.Select(b=>new CrossfireAction(b.BotOwner)).ToArray());
  }
  void Drive(Plugin p,BotComponent[] bots,CrossfireAction[] actions,int frames=65)
  {
   for(int i=0;i<frames;i++){
    foreach(var b in bots)p.Observe(b);
    for(int j=0;j<bots.Length;j++)if(new CrossfireLayer(bots[j].BotOwner,21).IsActive()){actions[j].Start();actions[j].Update(new());actions[j].OnSteeringTicked();}
    Step();
   }
  }
  foreach(int count in new[]{2,3,4}){
   var(p,bots,actions)=Setup(count);Drive(p,bots,actions);
   C(bots.All(b=>p.states[b].Crossfire.Policy.Trace?.Storm!=null),"S06 selected "+count);
   C(bots.Count(b=>b.Mover.Walks>0)>=2,"two actual native routes "+count+" "+string.Join(" / ",p.states.Values.Select(s=>s.Crossfire.Policy.Trace?.Reason)));
   C(bots.Where(b=>b.Mover.Walks>0).Select(b=>p.states[b].Crossfire.Goal).Distinct().Count()>=2,"separate approach points");
   C(bots.Where(b=>b.Mover.Walks>0).All(b=>Api.Decisions.Labels[b.ProfileId]=="COMET:CrossfireStormMoving"),"actual movement label");
   C(p.states.Values.All(s=>s.Crossfire.Maneuver==null),"no competing S03");
   var p1=p.states.Values.First(s=>s.Crossfire.Storm.Plan!=null).Crossfire.Storm.Plan!;C(p1.Departure>=10&&p1.Departure<=11.5,"bounded departure");
   foreach(var b in bots)C(b.ManualShoot.Requests==0,"no forced hidden shots");
   p.EndAll();C(p.Claims==0&&Api.Decisions.Labels.Count==0,"cleanup");events.AddRange(p.diagnostics!.Lines);
  }
  foreach(string mode in new[]{"non-pmc","human","ghost","remote","disabled","ammo","panic","heal","visible","stale","danger","unknown","no-route","no-callback","busy-path","grenade","moved-origin"}){
   var(p,bots,actions)=Setup();var b=bots[1];
   switch(mode){case "non-pmc":b.Pmc=false;break;case "human":b.Player.IsAI=false;break;case "ghost":p.Ghosts.Add(b.ProfileId);break;case "remote":p.Authority=false;break;case "disabled":p.config!.SquadStorm.Enabled=false;break;case "ammo":b.BotOwner.WeaponManager.CurrentWeapon.Mag.Count=1;break;case "heal":b.BotOwner.Medecine.Using=true;break;case "visible":b.GoalEnemy!.IsVisible=true;break;case "stale":b.GoalEnemy!.TimeSinceLastKnownUpdated=10;break;case "danger":p.StormEvidenceByBot[b.ProfileId].Danger=true;break;case "unknown":p.StormEvidenceByBot.Clear();break;case "no-route":b.Mover.Reachable=false;break;case "busy-path":b.Mover.ActivePath=new BotPathDataManual();break;case "grenade":b.Grenade.GrenadeDangerPoint=new(1,0,0);break;}
   if(mode=="panic"){p.Observe(b);p.states[b].Fear.Current.Panic=true;}
   if(mode=="no-callback"){
    for(int i=0;i<100;i++){foreach(var x in bots)p.Observe(x);if(new CrossfireLayer(bots[0].BotOwner,21).IsActive())actions[0].Update(new());Step();}
   }else if(mode=="moved-origin"){p.Observe(bots[0]);p.Observe(b);b.Position=new(70,0,0);Drive(p,bots,actions,100);}
   else Drive(p,bots,actions,100);
   C(bots.All(x=>x.Mover.Walks==0),"no group rush "+mode);C(!Api.Decisions.Labels.Values.Any(x=>x.Contains("Storm")),"no phantom ownership "+mode);events.AddRange(p.diagnostics!.Lines);p.EndAll();
  }
  foreach(string mode in new[]{"stalled","replaced","danger","visible-danger","expired","visible","authority","ammo","squad-change","body-obstruction","layer-lost"}){
   var(p,bots,actions)=Setup();Drive(p,bots,actions);var b=bots[0];var old=(BotPathDataManual)b.Mover.ActivePath!;var foreign=new BotPathDataManual{Destination=new(99,0,0)};
   switch(mode){case "replaced":b.Mover.ActivePath=foreign;break;case "danger":p.StormEvidenceByBot[b.ProfileId].Danger=true;break;case "visible-danger":b.GoalEnemy!.IsVisible=true;p.StormEvidenceByBot[b.ProfileId].Danger=true;break;case "visible":b.GoalEnemy!.IsVisible=true;break;case "authority":p.Authority=false;break;case "ammo":b.BotOwner.WeaponManager.CurrentWeapon.Mag.Count=1;break;case "squad-change":b.Squad.Members=new(){[b.ProfileId]=b};break;case "body-obstruction":p.Volume="new obstacle";break;case "expired":Step(5);break;case "layer-lost":actions[0].Stop();break;}
   for(int i=0;i<45&&p.states[b].Crossfire.Policy.Active;i++){Step(.1f);if(mode=="stalled")b.GoalEnemy!.TimeSinceLastKnownUpdated=0;actions[0].Update(new());actions[1].Update(new());}
   C(!p.states[b].Crossfire.Policy.Active,"release "+mode);C(!Api.Decisions.Labels.ContainsKey(b.ProfileId),"badge cleared "+mode);C(foreign.Stops==0,"foreign route preserved "+mode);
   if(mode=="visible-danger")C(bots.All(x=>!p.states[x].Crossfire.Policy.Active),"danger shared before handing visible participant to native combat");
   if(mode!="replaced")C(old.Stops==1,"owned route stopped "+mode);events.AddRange(p.diagnostics!.Lines);p.EndAll();
  }
  Directory.CreateDirectory("artifacts");File.WriteAllLines("artifacts/s06-adapter-events.jsonl",events);Console.WriteLine($"{n} S06 native-adapter checks passed; {events.Count} events");
 }
}
