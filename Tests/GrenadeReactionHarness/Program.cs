using Comet.Client;
using Comet.Core;
using EFT;
using SAIN.Components;
using SAIN.Models.Enums;
using SAIN.SAINComponent.SubComponents;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
using UnityEngine.AI;
int checks=0;void C(bool value,string why){checks++;if(!value)throw new Exception("G05 adapter: "+why);}
var streams=new List<string>();
(Plugin,BotComponent,GrenadeTrackerClass) Setup(string personality="Normal",bool detected=true){
 Time.Set(10);Physics.Hit=null;NavMesh.Sample=true;NavMesh.Route=null;NavMesh.Status=NavMeshPathStatus.PathComplete;NavMesh.Calls=0;
 var p=new Plugin();var b=new BotComponent{Position=new(2,0,0)};b.Info.Personality=personality;p.Add(b);
 var g=new Grenade{Id=-8,ProfileId="enemy"};var t=new GrenadeTrackerClass(b,g,new(0,0,0),1,true){CanReact=detected};b.Grenade.GrenadeReactionClass.EnemyGrenadesList.Add(g,t);return(p,b,t);
}
void Step(Plugin p,BotComponent b,float now){Time.Set(now);p.Tick(b);p.Act(b);}
var (p,b,t)=Setup(detected:false);Step(p,b,10);C(b.Grenade.GrenadeReactionClass.DangerGrenade==null&&b.Mover.Requests==0,"unspotted/reaction delay preserved");
t.CanReact=true;Step(p,b,10.2f);C(b.Grenade.GrenadeReactionClass.DangerGrenade==t&&p.EscapeTrace(b)?.State=="moving","detected grenade escapes through native action: "+p.EscapeTrace(b)?.State+" / "+p.EscapeTrace(b)?.Reason+" fault="+p.states[b].GrenadeEscape.Fault+" records="+string.Join(" | ",p.diagnostics!.Lines));
C(b.Mover.ActivePath!=null&&!b.Mover.Prone.Value,"feasible path beats generic expired fuse");int requests=b.Mover.Requests;
Step(p,b,10.4f);C(b.Mover.Requests==requests,"stable destination no repath");streams.AddRange(p.diagnostics!.Lines);
var previous=b.Mover.ActivePath;t.DangerPoint=new(12,0,0);Step(p,b,10.6f);C(!ReferenceEquals(b.Mover.ActivePath,previous),"rebound invalidates unsafe route before commitment");
foreach(var personality in new[]{"Normal","Chad","GigaChad","Wreckless","Rat","Coward","Timmy","SnappingTurtle"}){
 (p,b,t)=Setup(personality);b.Position=new(7,0,0);b.GoalEnemy=new(){KnownPlaces=new(){LastKnownPosition=new(30,0,0)},Source="seen",TimeSinceLastKnownUpdated=1};
 if(personality=="SnappingTurtle")b.Cover.CoverPoints.Add(new(){Position=new(15,0,3)});
 Step(p,b,10);C(p.EscapeTrace(b)?.Reaction==GrenadeReactionRules.Preference(personality,true),"personality "+personality);
 C(p.EscapeTrace(b)?.HasGoal==true,"safe route "+personality);streams.AddRange(p.diagnostics!.Lines);
}
(p,b,t)=Setup("Chad");b.GoalEnemy=new(){KnownPlaces=new(){LastKnownPosition=new(-30,0,0)},Source="seen"};Step(p,b,10);C(p.EscapeTrace(b)?.Reaction=="Scatter","aggression cannot cross grenade");
foreach(var source in new[]{"shared-seen","shared-heard","sain-last-known"}){
 (p,b,t)=Setup("Chad");b.GoalEnemy=new(){KnownPlaces=new(){LastKnownPosition=new(30,0,0)},Source=source};Step(p,b,10);C(p.EscapeTrace(b)?.Reaction=="Scatter","immediate close grenade takes priority over personality "+source);
}
(p,b,t)=Setup("Chad");b.GoalEnemy=new(){KnownPlaces=new(){LastKnownPosition=new(30,0,0)},Source="seen",TimeSinceLastKnownUpdated=4};Step(p,b,10);C(p.EscapeTrace(b)?.Reaction=="Scatter","close grenade scatter has no extra contact freshness veto");
(p,b,t)=Setup();Step(p,b,500);C(p.EscapeTrace(b)?.State=="moving","enemy tracker lifetime is not the own-grenade eight-second timer, even late in raid");
(p,b,t)=Setup();b.Grenade.GrenadeReactionClass.EstimatedTimeRemaining=.1f;Step(p,b,10);C(b.Mover.Prone.Value&&b.Mover.Requests==0,"source imminent blast chooses prone");
(p,b,t)=Setup();NavMesh.Route=(a,z)=>new[]{a,new Vector3(-2,0,0),z};
for(int i=0;i<7;i++){Step(p,b,10+i*.11f);C(NavMesh.Calls<=21*(i+1),"source seven bearings and three distances bound probes");}
C(b.Mover.Requests>0&&!b.Mover.Prone.Value,"source native route accepted without extra COMET danger-path scan");streams.AddRange(p.diagnostics!.Lines);
(p,b,t)=Setup();NavMesh.Status=NavMeshPathStatus.PathPartial;for(int i=0;i<7;i++)Step(p,b,10+i*.11f);C(b.Mover.Requests>0,"source delegates route acceptance to the native mover");
(p,b,t)=Setup();var second=new Grenade{Id=9,ProfileId="other"};var t2=new GrenadeTrackerClass(b,second,new(15,0,0),0,true){CanReact=true};b.Grenade.GrenadeReactionClass.EnemyGrenadesList.Add(second,t2);
for(int i=0;i<7;i++)Step(p,b,10+i*.11f);
C(p.EscapeTrace(b)?.Active==2,"multiple known dangers recorded");
C(b.Mover.Requests>0,"source chooses escape from native selected danger");
b.Grenade.GrenadeReactionClass.EnemyGrenadesList.Remove(t.Grenade);Step(p,b,12);C(p.states[b].GrenadeEscape.Dangers.Count==1,"one native track removed leaves other hazard");
(p,b,t)=Setup();Step(p,b,10);p.ghosts.Add(b.ProfileId);Step(p,b,10.2f);C(b.Mover.ActivePath==null&&p.EscapeTrace(b)?.ValidFor==0,"ghost cleanup");
foreach(var scope in new[]{"scav","client","disabled","inactive"}){
 (p,b,t)=Setup();if(scope=="scav")b.Pmc=false;if(scope=="client")p.Authority=false;if(scope=="disabled")p.config!.GrenadeReaction.Enabled=false;if(scope=="inactive")b.BotActive=false;
 Step(p,b,10);C(b.Mover.Requests==0&&p.Act(b),"native fallback "+scope);
}
(p,b,t)=Setup();p.config!.OwnGrenadeSafety.Enabled=false;Step(p,b,10);C(p.EscapeTrace(b)?.State=="moving","G05 independent of G04 toggle");
(p,b,t)=Setup();t.Grenade.WeaponSource!.ThrowType="smoke_grenade";Step(p,b,10);C(b.Mover.Requests==0,"smoke not treated as explosion");
(p,b,t)=Setup();Step(p,b,10);var owned=b.Mover.ActivePath;var foreign=new BotPathDataManual{PathFinder=b.Mover,NavMeshPath=new()};b.Mover.ActivePath=foreign;p.End();C(ReferenceEquals(b.Mover.ActivePath,foreign),"cleanup never cancels foreign path");C(p.states[b].GrenadeEscape.Dangers.Count==0,"raid clears references");
// AK_kN regression: a stale native AvoidGrenade must not consume the action
// when COMET has no live hazard. Use the production postfix without the mock
// native Update, which would otherwise erase the stale state before the test.
void NativeState(BotComponent bot,GrenadeTrackerClass? track,EGrenadeReaction reaction){
 var native=bot.Grenade.GrenadeReactionClass;
 native.GetType().GetProperty("DangerGrenade")!.SetValue(native,track);
 native.GetType().GetProperty("Reaction")!.SetValue(native,reaction);
}
void FilterOnly(Plugin plugin,BotComponent bot){
 typeof(Plugin).GetMethod("OwnNativeDanger",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!.Invoke(null,new object[]{bot.Grenade.GrenadeReactionClass});
}
foreach(var smoke in new Grenade[]{new SmokeGrenade(),new Grenade{GrenadeSettings=new(){CollisionSound=GrenadeSettings.CollisionSounds.smoke}}}){
 (p,b,t)=Setup();b.Grenade.GrenadeReactionClass.EnemyGrenadesList.Clear();
 t=new GrenadeTrackerClass(b,smoke,Vector3.zero,1,true){CanReact=true};b.Grenade.GrenadeReactionClass.EnemyGrenadesList.Add(smoke,t);
 NativeState(b,t,EGrenadeReaction.Scatter);FilterOnly(p,b);
 C(b.Grenade.GrenadeReactionClass.DangerGrenade==null&&b.Grenade.GrenadeReactionClass.Reaction==EGrenadeReaction.None,"source smoke ignored, including native collision-sound classification");
 C(p.Act(b)&&!p.states[b].GrenadeEscape.Handled,"smoke releases native action");
 C(p.diagnostics!.Lines.Any(x=>x.Contains("grenade-native-danger-cleared")),"native stale reaction repair logged");
}
(p,b,t)=Setup();b.Grenade.GrenadeReactionClass.EnemyGrenadesList.Clear();FilterOnly(p,b);
NativeState(b,t,EGrenadeReaction.Scatter);Time.Set(10.2f);FilterOnly(p,b);
C(p.Act(b)&&b.Grenade.GrenadeReactionClass.Reaction==EGrenadeReaction.None,"stale removed tracker cannot keep AvoidGrenade active");
int repairLogs=p.diagnostics!.Lines.Count(x=>x.Contains("native-reaction"));
for(int i=0;i<100;i++){NativeState(b,t,EGrenadeReaction.Scatter);Time.Set(10.4f+i*.2f);FilterOnly(p,b);}
C(p.diagnostics!.Lines.Count(x=>x.Contains("native-reaction"))==repairLogs,"repeated same native mismatch does not spam diagnostics");
(p,b,t)=Setup();Step(p,b,10);b.Grenade.GrenadeReactionClass.EnemyGrenadesList.Clear();Step(p,b,10.2f);
C(p.Act(b)&&b.Mover.ActivePath==null&&!p.states[b].GrenadeEscape.Handled,"last live hazard removal stops owned path and releases native action");
(p,b,t)=Setup();b.Grenade.GrenadeReactionClass.EstimatedTimeRemaining=.1f;Step(p,b,10);C(b.Mover.Prone.Value,"test reaches source imminent-blast prone");
b.Grenade.GrenadeReactionClass.EnemyGrenadesList.Clear();Step(p,b,10.2f);C(!b.Mover.Prone.Value&&p.Act(b),"expired prone does not leave bot pinned");
(p,b,t)=Setup();Step(p,b,10);p.states[b].GrenadeEscape.Hazard=null;C(p.Act(b)&&!p.states[b].GrenadeEscape.Handled,"hazard disappears between selection and action: return to native");
(p,b,t)=Setup();b.Position=new(19,0,0);Step(p,b,10);C(p.Act(b)&&b.Grenade.GrenadeReactionClass.DangerGrenade==null,"source 18m native reaction range");
(p,b,t)=Setup();Step(p,b,10);p.End();C(p.states[b].GrenadeEscape.ReportedDanger==null&&p.states[b].GrenadeEscape.Live.Count==0,"cleanup releases cached diagnostic and hazard references");
var artifacts=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../artifacts"));Directory.CreateDirectory(artifacts);
File.WriteAllLines(Path.Combine(artifacts,"g05-adapter-events.jsonl"),streams.Where(l=>l.Contains("\"feature\":\"G05\"")));
Console.WriteLine($"G05 production adapter: {checks} checks passed (controlled engine doubles, not a raid).");
