using Comet.Client;
using Comet.Core;
using EFT;
using SAIN.Components;
using SAIN.SAINComponent.SubComponents;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
using UnityEngine.AI;
int checks=0;void C(bool v,string why){checks++;if(!v)throw new Exception("G06 adapter: "+why);}
var stream=new List<string>();
(Plugin,BotComponent[]) Setup(int count=2){
 Time.Set(10);Physics.Hit=null;NavMesh.Sample=true;NavMesh.Route=null;NavMesh.Status=NavMeshPathStatus.PathComplete;NavMesh.Calls=0;
 var p=new Plugin();var group=new Squad{SquadInfo=new(){GUID="team"}};
 var bots=Enumerable.Range(0,count).Select(i=>new BotComponent{ProfileId="pmc-"+i,Position=new(2,0,0),Squad=group}).ToArray();
 var grenade=new Grenade{Id=-8,ProfileId="enemy"};
 foreach(var b in bots){p.Add(b);group.Members.Add(b.ProfileId,b);b.Grenade.GrenadeReactionClass.EnemyGrenadesList.Add(grenade,new GrenadeTrackerClass(b,grenade,new(0,0,0),0,true){CanReact=true});}
 return(p,bots);
}
void Step(Plugin p,BotComponent b,float now){Time.Set(now);p.Tick(b);p.Act(b);}
var(p,bots)=Setup();var a=bots[0];var b=bots[1];
Step(p,a,10);Step(p,b,10.01f);
C(p.states[a].GrenadeEscape.DispersionLease!=null,"first member reserves accepted route");
C(Vector3.Distance(p.states[a].GrenadeEscape.Goal,p.states[b].GrenadeEscape.Goal)>=3,"second member selects another safe point");
C(p.states[b].GrenadeEscape.DispersionTrace?.Peers==1,"participating peer recorded");
C(p.states[b].GrenadeEscape.DispersionTrace!.Alternatives.Any(x=>x.Contains("pmc-0")),"rejected reserved candidate names peer");
var held=b.Mover.ActivePath;var token=p.states[b].GrenadeEscape.DispersionLease!.Token;Step(p,b,10.3f);
C(ReferenceEquals(held,b.Mover.ActivePath)&&p.states[b].GrenadeEscape.DispersionLease!.Token==token,"stable safe route and reservation");
C(p.states[b].GrenadeEscape.DispersionLease!.Expires>11.2,"native callback renews short lease");stream.AddRange(p.diagnostics!.Lines);

// Every available safe sample funnels into a single corridor exit: no additional tick waiting for spacing.
(p,bots)=Setup();a=bots[0];b=bots[1];Step(p,a,10);NavMesh.Route=(x,z)=>Math.Abs(z.z)<.01?new[]{x,z}:new[]{x,new Vector3(-2,0,0),z};
int calls=NavMesh.Calls;Step(p,b,10.01f);
C(b.Mover.ActivePath!=null&&!b.Mover.Prone.Value,"narrow exit fallback moves in same callback");
C(p.states[b].GrenadeEscape.DispersionTrace?.State=="shared","shared safe exit reported honestly");
C(NavMesh.Calls-calls<=4,"no extra per-bot navigation budget");stream.AddRange(p.diagnostics!.Lines);

// Safe destination on another floor must not be considered occupied by the same horizontal coordinates.
(p,bots)=Setup();Step(p,bots[0],10);bots[1].Position=new(2,3,0);p.config!.GrenadeDispersion.Spacing=8;Step(p,bots[1],10.01f);
C(p.states[bots[1]].GrenadeEscape.DispersionTrace?.State=="reserved"&&p.states[bots[1]].GrenadeEscape.DispersionTrace!.Nearest<0,"separate floor not penalized");

foreach(var exclusion in new[]{"dead","ghost","non-pmc","no-track","foreign-path","expired","other-squad","distant","identity"}){
 (p,bots)=Setup();a=bots[0];b=bots[1];Step(p,a,10);
 if(exclusion=="dead")a.IsDead=true;if(exclusion=="ghost")p.ghosts.Add(a.ProfileId);if(exclusion=="non-pmc")a.Pmc=false;
 if(exclusion=="no-track")p.states[a].GrenadeEscape.Selected=null;
 if(exclusion=="foreign-path")a.Mover.ActivePath=new BotPathDataManual{PathFinder=a.Mover};
 if(exclusion=="expired")p.states[a].GrenadeEscape.DispersionLease!.Expires=10;
 if(exclusion=="other-squad")a.Squad=new(){SquadInfo=new(){GUID="other"}};
 if(exclusion=="distant")a.Position=new(100,0,0);
 if(exclusion=="identity")p.states[a].GrenadeEscape.DispersionLease!.Instance="old-instance";
 Step(p,b,10.1f);C(p.states[b].GrenadeEscape.DispersionTrace?.Peers==0,"exclude "+exclusion);
}
(p,bots)=Setup();Step(p,bots[0],10);Time.Set(10.8f);p.Tick(bots[0]);C(p.states[bots[0]].GrenadeEscape.DispersionLease!.Expires==11,"decision callback alone cannot renew");
Step(p,bots[1],11.1f);C(p.states[bots[1]].GrenadeEscape.DispersionTrace?.Peers==0,"stale owner ignored without sweeper");

foreach(var disabled in new[]{"g06","g05","authority"}){
 (p,bots)=Setup();if(disabled=="g06")p.config!.GrenadeDispersion.Enabled=false;if(disabled=="g05")p.config!.GrenadeReaction.Enabled=false;if(disabled=="authority")p.Authority=false;
 Step(p,bots[0],10);C(p.states[bots[0]].GrenadeEscape.DispersionLease==null,"no reservation "+disabled);
 if(disabled=="g06")C(bots[0].Mover.ActivePath!=null,"G05 unchanged with G06 off");
}
(p,bots)=Setup();bots[1].Grenade.GrenadeReactionClass.EnemyGrenadesList.Values.First().CanReact=false;Step(p,bots[0],10);Step(p,bots[1],10.1f);
C(bots[1].Mover.ActivePath==null&&p.states[bots[1]].GrenadeEscape.DispersionLease==null,"no transferred grenade awareness or reaction shortcut");

(p,bots)=Setup(6);foreach(var bot in bots)Step(p,bot,10+Array.IndexOf(bots,bot)*.01f);
C(bots.All(x=>x.Mover.ActivePath!=null),"six members get safe routes without a reservation queue");
C(bots.All(x=>p.states[x].GrenadeEscape.Goal.x>0),"all routes still away from danger");
// Two native actions in one frame share the same eight-probe limit.
(p,bots)=Setup(3);Time.Set(10);foreach(var bot in bots){p.Tick(bot);p.Act(bot);}C(NavMesh.Calls<=8,"global frame budget retained");

(p,bots)=Setup();Step(p,bots[0],10);var danger=new Grenade{Id=9,ProfileId="other"};b=bots[1];b.Grenade.GrenadeReactionClass.EnemyGrenadesList.Add(danger,new GrenadeTrackerClass(b,danger,new(13,0,-9),0,true){CanReact=true});
Step(p,b,10.1f);C(b.Mover.ActivePath==null||(p.states[b].GrenadeEscape.Goal-new Vector3(13,0,-9)).magnitude>=11,"spacing cannot bypass another grenade");
(p,bots)=Setup();Step(p,bots[0],10);p.ghosts.Add(bots[0].ProfileId);Step(p,bots[0],10.1f);C(p.states[bots[0]].GrenadeEscape.DispersionLease==null&&p.states[bots[0]].GrenadeEscape.DispersionTrace?.State=="ended","ghost releases reservation and RR context");
stream.AddRange(p.diagnostics!.Lines);
(p,bots)=Setup();Step(p,bots[0],10);p.End();C(p.states.Values.All(s=>s.GrenadeEscape.DispersionLease==null&&s.GrenadeEscape.DispersionPeers.Count==0),"raid cleanup releases all reservations");stream.AddRange(p.diagnostics!.Lines);
(p,bots)=Setup();bots[0].Mover.Accept=false;Step(p,bots[0],10);C(p.states[bots[0]].GrenadeEscape.DispersionLease==null,"native refusal never reserves a destination");
(p,bots)=Setup();Step(p,bots[0],10);a=bots[0];var originalToken=p.states[a].GrenadeEscape.DispersionLease!.Token;
a.Grenade.GrenadeReactionClass.EnemyGrenadesList.Values.First().DangerPoint=new(12,0,0);Step(p,a,10.2f);
C(p.states[a].GrenadeEscape.DispersionLease?.Token!=originalToken,"rebound releases old destination before choosing another");
(p,bots)=Setup();Step(p,bots[0],10);a=bots[0];var foreign=new BotPathDataManual{PathFinder=a.Mover};a.Mover.ActivePath=foreign;p.End();
C(ReferenceEquals(a.Mover.ActivePath,foreign)&&p.states[a].GrenadeEscape.DispersionLease==null,"teardown preserves foreign path but drops own reservation");
var artifacts=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../artifacts"));Directory.CreateDirectory(artifacts);File.WriteAllLines(Path.Combine(artifacts,"g06-adapter-events.jsonl"),stream.Where(s=>s.Contains("\"feature\":\"G06\"")));
Console.WriteLine($"G06 production adapter: {checks} checks passed (controlled engine doubles, not a raid).");
