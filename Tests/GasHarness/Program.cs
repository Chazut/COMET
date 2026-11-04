using Comet.Client;
using Manimal.CSGas.Gas;
using BepInEx.Bootstrap;
using SAIN.Components;
using SAIN.SAINComponent.Classes.Decision;
using UnityEngine;
using UnityEngine.AI;
int checks=0;void C(bool v,string why){checks++;if(!v)throw new Exception(why);}
var streams=new List<string>();
(Plugin,BotComponent,BotDecisionManager,CsGasCloud) Setup(){Time.Set(10);Physics.Wall=false;NavMesh.Sample=true;NavMesh.Status=NavMeshPathStatus.PathComplete;NavMesh.Route=null;NavMesh.Calls=0;Chainloader.PluginInfos.Clear();Chainloader.PluginInfos.Add(ManimalGasBridge.Guid,new());var p=new Plugin();var b=new BotComponent();var c=new CsGasCloud();Plugin.GasCloudCreated(c);return(p,b,new(b),c);}
void Step(Plugin p,BotComponent b,BotDecisionManager m,float t){Time.Set(t);p.Tick(b);p.Decide(m);p.Act(b);}
Chainloader.PluginInfos.Clear();var absent=new Plugin();C(!absent.Ready,"absent dormant");var x=new BotComponent();absent.Tick(x);C(absent.states.Count==0,"absent no bot bookkeeping");
Chainloader.PluginInfos[ManimalGasBridge.Guid]=new(){Metadata=new(){Version=new(2,1,0)}};C(!new Plugin().Ready,"unknown future version inactive");
Chainloader.PluginInfos[ManimalGasBridge.Guid]=new(){Instance=null!};C(!new Plugin().Ready,"provider failed to load stays inactive");
var broken=new ManimalGasBridge();C(!broken.Bind(typeof(string).Assembly,new(2,0,0)),"wrong contract inactive");
var(p,b,m,c)=Setup();C(p.Ready,"compatible");Step(p,b,m,10);C(p.State(b).Inside&&p.State(b).Owned&&m.Decisions==1,"native avoidance without enemy");C(p.State(b).Path!=null&&p.State(b).Trace?.State=="escaping","reachable escape");
int calls=b.Mover.Requests;Step(p,b,m,10.25f);C(b.Mover.Requests==calls,"stable route");C(p.State(b).Intensity>0&&b.BotOwner.Settings.Current.Active.Count==1,"reversible modifiers");C(b.Talk.Count==1,"cough");
Step(p,b,m,10.5f);C(b.Talk.Count==1,"cough interval");streams.AddRange(p.diagnostics!.Lines);
b.Player.Protection="Respirator";Step(p,b,m,10.75f);C(p.State(b).Modifier?.Hearing==1&&b.Talk.Count==1,"respirator protects lungs but not eyes");
b.Player.Protection="Full";Step(p,b,m,11);C(!p.State(b).Owned&&p.State(b).Path==null,"full mask stops escape");
for(int i=1;i<40;i++)Step(p,b,m,11+i*.25f);C(p.State(b).Intensity==0&&b.BotOwner.Settings.Current.Active.Count==0,"masked recovery removes only own modifiers");
(p,b,m,c)=Setup();Physics.Wall=true;Step(p,b,m,10);C(!p.State(b).Inside,"sphere wall blocked");
(p,b,m,c)=Setup();c._exploded=false;Step(p,b,m,10);C(!p.State(b).Inside,"not emitted yet");
(p,b,m,c)=Setup();c.gameObject.activeInHierarchy=false;Step(p,b,m,10);C(!p.State(b).Inside,"inactive cloud ignored");
(p,b,m,c)=Setup();var emitter=new VolumetricGasEmitter{Test=v=>v.x<3};c._emitter=emitter;Plugin.GasEmitterCreated(emitter);Physics.Wall=true;Step(p,b,m,10);C(p.State(b).Inside,"provider volumetric shape authoritative");c.Dead=true;Step(p,b,m,10.25f);C(p.State(b).Inside,"lingering emitter survives canister");emitter._finished=true;emitter._emitUntil=10;emitter._particleLifetime=.5f;Step(p,b,m,10.75f);C(!p.State(b).Inside&&!p.State(b).Owned,"actual expiry");
(p,b,m,c)=Setup();Step(p,b,m,10);b.Grenade.GrenadeReactionClass.Danger=true;Step(p,b,m,10.25f);C(!p.State(b).Owned&&p.State(b).Path==null,"frag wins independently");C(p.Decide(m),"native grenade retains decision");b.Grenade.GrenadeReactionClass.Danger=false;Step(p,b,m,10.5f);C(p.State(b).Owned,"gas resumes after frag");
(p,b,m,c)=Setup();Step(p,b,m,10);Step(p,b,m,15.1f);C(!p.State(b).Owned&&p.State(b).Trace?.State=="retry","bounded attempt");Step(p,b,m,16);C(!p.State(b).Owned,"retry delay");Step(p,b,m,17.2f);C(p.State(b).Owned,"persistent gas retry");
(p,b,m,c)=Setup();NavMesh.Sample=false;for(int i=0;i<13;i++)Step(p,b,m,10+i*.25f);C(!p.State(b).Owned&&p.diagnostics!.Lines.Any(x=>x.Contains("gas-no-reachable-exit")),"no route bounded");streams.AddRange(p.diagnostics!.Lines);
(p,b,m,c)=Setup();NavMesh.Status=NavMeshPathStatus.PathPartial;Step(p,b,m,10);C(b.Mover.Requests==0,"partial navigation rejected");
(p,b,m,c)=Setup();Step(p,b,m,10);var foreign=new SAIN.SAINComponent.Classes.Mover.PathData();b.Mover.ActivePath=foreign;p.End();C(!foreign.Stopped,"foreign path preserved");C(b.BotOwner.Settings.Current.Active.Count==0,"raid modifiers cleared");
foreach(string mode in new[]{"ghost","scav","disabled","client","inactive","dead","game-ending"}){
 (p,b,m,c)=Setup();Step(p,b,m,10);Step(p,b,m,10.25f);if(mode=="ghost")p.ghosts.Add(b.ProfileId);if(mode=="scav")b.Pmc=false;if(mode=="disabled")p.config!.GasExposure.Enabled=false;if(mode=="client")p.Authority=false;if(mode=="inactive")b.BotActive=false;if(mode=="dead")b.IsDead=true;if(mode=="game-ending")b.GameEnding=true;
 Step(p,b,m,10.5f);C(!p.State(b).Owned&&p.State(b).Intensity==0&&b.BotOwner.Settings.Current.Active.Count==0,"scope cleanup "+mode);
}
(p,b,m,c)=Setup();Step(p,b,m,10);p.End();Step(p,b,m,11);C(!p.State(b).Inside,"registry clear across raids");
(p,b,m,c)=Setup();var c2=new CsGasCloud();Plugin.GasCloudCreated(c2);Step(p,b,m,10);Step(p,b,m,10.25f);C(Math.Abs(p.State(b).Intensity-.0625)<.00001,"overlap does not stack");
(p,b,m,c)=Setup();Step(p,b,m,10);var owned=p.State(b).Path as SAIN.SAINComponent.Classes.Mover.PathData;p.StopAction(b);C(owned?.Stopped==true&&!p.State(b).Owned,"native action stop releases owned path");
(p,b,m,c)=Setup();Step(p,b,m,10);b.Player.HandsController.Busy=true;Step(p,b,m,10.25f);C(!p.State(b).Owned,"hands priority");
(p,b,m,c)=Setup();Step(p,b,m,10);b.Flashed.IsFlashed=true;Step(p,b,m,10.25f);C(!p.State(b).Owned,"real flash priority, gas never sets flash state");
(p,b,m,c)=Setup();NavMesh.Route=(a,z)=>[a,new(7,0,0),new(1,0,0),z];Step(p,b,m,10);C(b.Mover.Requests==0,"route reentry rejected");
(p,b,m,c)=Setup();for(int i=0;i<12;i++){var bot=new BotComponent{ProfileId="pmc"+i};p.Tick(bot);p.Decide(new(bot));p.Act(bot);}C(NavMesh.Calls<=8,"shared navigation budget");
(p,b,m,c)=Setup();var native=b.Grenade.GrenadeReactionClass;var gasTrack=new SAIN.SAINComponent.SubComponents.GrenadeTrackerClass{Grenade=c._smoke};native.Select(gasTrack);native.EnemyGrenadesList[1]=gasTrack;p.Filter(b);C(native.DangerGrenade==null,"verified CS not an explosive");Step(p,b,m,10);C(p.State(b).Owned,"CS native avoidance no longer blocks gas escape");
var frag=new SAIN.SAINComponent.SubComponents.GrenadeTrackerClass{Grenade=new EFT.Grenade(),DangerPoint=new(6,0,0)};native.EnemyGrenadesList[2]=frag;native.Select(gasTrack);p.Filter(b);C(ReferenceEquals(native.DangerGrenade,frag),"nearby detected frag selected independently of CS");
frag.CanReact=false;native.Select(gasTrack);p.Filter(b);C(native.DangerGrenade==null,"unperceived frag not invented");frag.CanReact=true;frag.DangerPoint=new(50,0,0);native.Select(gasTrack);p.Filter(b);C(native.DangerGrenade==null,"native 18m selection retained");
p.config!.GasExposure.Enabled=false;native.Select(gasTrack);p.Filter(b);C(ReferenceEquals(native.DangerGrenade,gasTrack),"disabled feature leaves native reaction unchanged");
var dir=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../artifacts"));File.WriteAllLines(Path.Combine(dir,"g08-adapter-events.jsonl"),streams);Console.WriteLine($"G08 production adapter + provider bridge: {checks} checks passed (controlled engine doubles, not a raid).");
