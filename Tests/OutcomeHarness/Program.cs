using Comet.Client;
using Comet.Core;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using UnityEngine;
using System.Text.Json;

int checks=0;void Check(bool v,string reason){checks++;if(!v)throw new Exception("E04 adapter: "+reason);}
foreach(string gate in new[]{"none","disabled","client","scav","human","ghost","dead","inactive","ending","self","squad","native-other","same-enum-other-action","no-label","diagnostic-fault"})
{
    var cp=new Plugin();var cb=new BotComponent();var cs=new BotState{Mixing=new(){Encounter=1,Opportunity=1}};
    if(gate=="disabled")cp.config!.TacticCounters.Enabled=false;if(gate=="client")cp.Authority=false;if(gate=="scav")cb.Pmc=false;if(gate=="human")cb.Player.IsAI=false;
    if(gate=="ghost")cp.IsGhost=true;if(gate=="dead")cb.IsDead=true;if(gate=="inactive")cb.BotActive=false;if(gate=="ending")cb.GameEnding=true;
    if(gate=="self")cb.Decision.CurrentSelfDecision=ESelfActionType.FirstAid;if(gate=="squad")cb.Decision.CurrentSquadDecision=ESquadDecision.Other;
    if(gate=="native-other")cb.Decision.CurrentCombatDecision=ECombatDecision.Freeze;if(gate=="same-enum-other-action")cb.CurrentAction=new object();if(gate=="no-label")cs.ActiveLabel="";
    if(gate=="diagnostic-fault")cp.ThrowCounterEmit=true;
    for(int i=0;i<20;i++)cp.CounterTick(cb,cs);
    Check(cp.CounterEvents==(gate=="none"?1:0),"T02 observes one real start, respecting "+gate);
    if(gate=="none"){cs.Mixing.Opportunity++;cp.CounterTick(cb,cs);Check(cp.CounterEvents==2,"T02 new choice can observe another start");}
    if(gate=="diagnostic-fault")Check(cs.CounterObserverFailed&&cs.ActiveLabel=="COMET:Cover"&&cb.Decision.CurrentCombatDecision==ECombatDecision.SeekCover&&cp.Logger.Warnings.Count==1,"T02 diagnostic failure cannot release combat or spam logs");
}
(Plugin p,BotComponent b,BotState s) Setup(){var p=new Plugin();Plugin.Instance=p;var b=new BotComponent();var s=new BotState();p.states[b]=s;return(p,b,s);}
{
 var flank=Setup();
 Check(flank.p.Destination(flank.b,flank.s,"Flank")==null,"flank without a selected point cannot borrow a different cover destination");
 flank.s.Flank.Point=new(){Position=new(8,0,0)};
 Check(flank.p.Destination(flank.b,flank.s,"Flank")?.X==8,"outcome follows the exact flank destination");
 Check(flank.p.Destination(flank.b,flank.s,"Cover")?.X==2,"regular cover destination stays independent");
}
void Tick(Plugin p,BotComponent b,BotState s,float t,bool probe=true){Time.time=Time.realtimeSinceStartup=t;Time.frameCount++;if(probe&&s.Probe!=null){s.Probe.At=t;s.Probe.Origin=b.Position;}p.Tick(b,s);}
void Good(Plugin p,BotComponent b,BotState s,float start){b.Position=default;b.Cover.CoverInUse=null;s.Probe!.Current=.8;Tick(p,b,s,start);b.Position=new(2,0,0);b.Cover.CoverInUse=new();s.Probe.Current=.1;for(int i=1;i<=13;i++)Tick(p,b,s,start+i*.125f);}
var (p,b,s)=Setup();Good(p,b,s,0);Check(s.Outcomes.Memory.Trace.Result=="favorable","native confirmed protective cover");Check(p.diagnostics!.Lines.Count>2,"production diagnostics emitted");
foreach(var line in p.diagnostics.Lines){using var doc=JsonDocument.Parse(line);var root=doc.RootElement;Check(root.GetProperty("feature").GetString()=="E04"&&root.GetProperty("outcome").GetProperty("target").GetString()=="enemy","wire envelope");}
// One long action must not count as repeated successful trials.
for(int i=14;i<=90;i++)Tick(p,b,s,i*.125f);Check(s.Outcomes.Memory.HistoryCount==1,"native action remains one episode");
// Exercise a real detached action boundary and then a comparable repeated attempt.
s.ActiveLabel="";for(int i=91;i<=98;i++)Tick(p,b,s,i*.125f);s.ActiveLabel="COMET:Cover";Good(p,b,s,12.5f);
var t=p.Read(b,s)!;Check(t.Effects.Single(x=>x.Action=="Cover").Delta==5,"repeated nearby approach applies bounded bonus");
s.Situation=new(){Target="enemy",IsAi=true,IsSupportedBot=true,IsActive=true,IsAuthority=true,Outcome=t};s.Candidates=new(){new(){Decision="Cover",Eligible=true,Score=60}};
OutcomeScoring.Adjust(s.Candidates,s.Situation,p.config!);p.Choice(s,s.Situation);Check(t.Choice=="Cover"&&t.Effects.Single(x=>x.Action=="Cover").Applied==5&&t.Alternatives.Length==1,"score and alternatives diagnostic");
string rootDir=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));string output=Path.Combine(rootDir,"artifacts/e04");Directory.CreateDirectory(output);
File.WriteAllLines(Path.Combine(output,"outcomes.jsonl"),p.diagnostics.Lines);
var fixture=p.diagnostics.Lines.First(x=>JsonDocument.Parse(x).RootElement.GetProperty("outcome").GetProperty("result").GetString()=="favorable");
File.WriteAllText(Path.Combine(output,"e04-outcome.json"),fixture);
File.WriteAllText(Path.Combine(output,"e04-choice.json"),Newtonsoft.Json.JsonConvert.SerializeObject(new{schema=1,session="e04-harness",sequence=999,feature="A02",stage="selected",decision="Cover",profileId="observer",botInstance="native-observer",outcome=t},Diagnostics.Json));
foreach(var edit in new Action<Plugin,BotComponent,BotState>[] {
 (p,b,s)=>b.Pmc=false,(p,b,s)=>b.Player.IsAI=false,(p,b,s)=>p.Authority=false,(p,b,s)=>p.IsGhost=true,
 (p,b,s)=>b.BotActive=false,(p,b,s)=>b.IsDead=true,(p,b,s)=>b.GameEnding=true,(p,b,s)=>p.config!.Outcomes.Enabled=false,
 (p,b,s)=>b.GoalEnemy=null,(p,b,s)=>b.EnemyController.KnownEnemies.Clear()})
{var a=Setup();Good(a.p,a.b,a.s,0);edit(a.p,a.b,a.s);Tick(a.p,a.b,a.s,1.8f);Check(a.s.Outcomes.Memory.HistoryCount==0&&!a.s.Outcomes.Memory.HasAttempt,"scope/authority/death/ghost/contact cleanup");}
foreach(var edit in new Action<BotComponent,BotState>[] {
 (b,s)=>s.ActiveLabel="",(b,s)=>b.CurrentAction=new object(),(b,s)=>b.SAINLayersActive=false,(b,s)=>s.Expected=ECombatDecision.RushEnemy,
 (b,s)=>b.Decision.CurrentSelfDecision=ESelfActionType.FirstAid,(b,s)=>b.BotOwner.DoorOpener.Interacting=true,
 (b,s)=>s.Feature="D01",(b,s)=>s.Probe=null})
{var a=Setup();edit(a.b,a.s);if(a.s.Probe==null){Tick(a.p,a.b,a.s,0);a.b.Position=new(2,0,0);a.b.Cover.CoverInUse=new();for(int i=1;i<30;i++)Tick(a.p,a.b,a.s,i*.125f);}else Good(a.p,a.b,a.s,0);Check(a.s.Outcomes.Memory.HistoryCount==0,"intent/unowned/no exposure cannot establish success");}
foreach(bool ambiguous in new[]{false,true}) {
 var a=Setup();a.s.Decision="Push";a.s.Expected=a.b.Decision.CurrentCombatDecision=ECombatDecision.RushEnemy;a.b.CurrentAction=new RushEnemyAction();
 Tick(a.p,a.b,a.s,0);Time.time=.05f;a.p.Hit(a.b,20,ambiguous?"unknown":"enemy");a.b.Player.HealthController.Value=.8f;
 a.b.Decision.CurrentCombatDecision=ECombatDecision.Retreat;a.b.CurrentAction=new SeekCoverAction();Tick(a.p,a.b,a.s,.125f);
 Check(a.s.Outcomes.Memory.Trace.Result==(ambiguous?"neutral":"unfavorable"),"perceived shooter qualification with actual native retreat");
}
var fail=Setup();Tick(fail.p,fail.b,fail.s,0);fail.b.Player.HealthController.Throw=true;Tick(fail.p,fail.b,fail.s,.125f);Tick(fail.p,fail.b,fail.s,.25f);
Check(fail.s.Outcomes.Fault&&fail.p.Logger.Warnings.Count==1&&!fail.s.Outcomes.Memory.HasAttempt,"local failure contained and cleaned");
p.End(s);Check(s.Outcomes.Memory.HistoryCount==0&&p.diagnostics.Lines.Last().Contains("outcome-raid-ended"),"explicit terminal telemetry");
Console.WriteLine($"E04 production adapter: {checks} checks passed; output {output}. Boundary doubles, not a Unity raid.");
