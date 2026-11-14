using System.Collections;
using System.Runtime.CompilerServices;
using Comet.Client;
using Comet.Core;
using Newtonsoft.Json.Linq;

int checks=0;void Check(bool value,string why){checks++;if(!value)throw new Exception(why);}
var events=new List<string>();Comet.Api.Decisions.Published+=events.Add;
var p=new Plugin();var oldWorld=new EFT.GameWorld();p.Begin(oldWorld);
var oldEpoch=p.Epoch;
using var fetchStarted=new ManualResetEventSlim();using var releaseFetch=new ManualResetEventSlim();
p.loading.Start(oldEpoch,()=>{fetchStarted.Set();releaseFetch.Wait();return new CometConfig{Revision=7};});
Check(fetchStarted.Wait(3000),"old HTTP task started");
var ev=p.Subscribe();ev.FailThrowRemove=true;
for(int i=0;i<4;i++){p.states.Add(new Bot{Fail=i==0},new());var owned=new Owned{Fail=i==1};owned.Refs.Add(new object());p.clearOwnGrenades.Add(owned);}
p.maintenanceSnapshot.AddRange(p.states);
p.supportOperations.Add(new(),new(){Fail=true});p.movementPending.Add(new(),new(){Fail=true});
p.motionWatches.Refs.Add(oldWorld);p.spatialPanels[0]=oldWorld;p.quietCaller=oldWorld;
Comet.Api.DoorMotionNetwork.Fail=true;
var published=new List<object>();var iterator=new Native{Fail=true};var scan=MakeScan(iterator,published,()=>oldWorld);
p.coverScans.Add(new(),new(){Runner=scan});scan.MoveNext();
p.End();
Check(p.Cleaned==4,"all bots cleaned after earlier failures");
Check(p.states.Count==0&&p.maintenanceSnapshot.Count==0&&p.coverScans.Count==0&&p.clearOwnGrenades.Count==0,"owned bot/scan/tracker registries cleared");
Check(p.supportOperations.Count==0&&p.movementPending.Count==0,"failed operations do not keep owners");
Check(p.world==null&&p.config==null&&!p.loading.Pending,"raid world/config detached");
Check(p.LightClears==1,"raid cleanup invokes light detection cache cleanup");
Check(ev.Count==0&&p.clearEvents==null,"both event handlers detached despite one failure");
Check(p.motionWatches.Refs.Count==0&&p.spatialPanels[0]==null&&p.quietCaller==null,"audited secondary roots cleared");
Check(iterator.Disposed==1&&!scan.MoveNext(),"disposed native search cannot resume even when Dispose throws");
var json=JObject.Parse(events.Single());var trace=json["raidCleanup"]!;
Check(trace["failedSteps"]!.Value<int>()>=6&&trace["remainingBots"]!.Value<int>()==0,"partial cleanup failures reported honestly");
Check(trace["pendingConfigDiscarded"]!.Value<bool>(),"detached config reported");
Check(!p.CanBegin(oldWorld)&&!p.Allowed("LateCallback")&&p.Allowed("Update"),"old world cannot restart and late operations denied");
Comet.Api.Decisions.Publish("late callback",_=>{});Check(events.Count==1,"late telemetry cannot repopulate a finished raid");
int cleared=Comet.Api.DoorMotionNetwork.Cleared;p.End();p.WorldDisposed(oldWorld);
Check(events.Count==1&&cleared==Comet.Api.DoorMotionNetwork.Cleared,"cleanup idempotent");
releaseFetch.Set();p.Begin(new());p.loading.Start(p.Epoch,()=>new CometConfig{Revision=11});
CometConfig? current=null;Exception? error=null;
Check(SpinWait.SpinUntil(()=>p.loading.Take(p.Epoch,out current,out error),3000)&&current?.Revision==11,"new raid never takes old response");
p.WorldDisposed(oldWorld);Check(p.config!=null,"old dispose callback cannot close new raid");
Comet.Api.DoorMotionNetwork.Fail=false;p.End();
for(int i=0;i<24;i++){p.Begin(new());p.states.Add(new(),new());p.End();}
Check(events.Count==26&&p.Cleaned==28,"24 subsequent cycles clean and report once each");
var weak=Collectible(p);GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
Check(weak.All(w=>!w.IsAlive),"old world, iterator capture and bot collectible while Plugin and disposed wrapper stay alive");
GC.KeepAlive(p);GC.KeepAlive(Keep.Scan);
var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../artifacts/c13"));Directory.CreateDirectory(root);
File.WriteAllLines(Path.Combine(root,"cleanup.jsonl"),events);File.WriteAllText(Path.Combine(root,"cleanup.json"),events[0]);
var diagnostic=new Diagnostics(root,"harness-"+Guid.NewGuid().ToString("N"),_=>{});diagnostic.Write(events[0]);diagnostic.Dispose();diagnostic.Dispose();
Check(diagnostic.Completion.Wait(3000),"diagnostic queue drains without a gameplay-thread wait in Dispose");
Console.WriteLine($"C13 production cleanup coordinator: {checks} checks, {events.Count} reports. Native engine calls doubled.");

static CoverScanRunner<object> MakeScan(Native native,List<object> output,Func<object> owner)=>new(native,output,
 ()=>new Native(),()=>{GC.KeepAlive(owner());return 0;},()=>"enemy",()=>1,()=>10,_=>false,(_,_)=>{},_=>{},_=>{},new());
// Retain the disposed wrapper as SAIN may do, without retaining its captured owner.
[MethodImpl(MethodImplOptions.NoInlining)]
static WeakReference[] Collectible(Plugin p){var w=new EFT.GameWorld();p.Begin(w);var b=new Bot();p.states.Add(b,new());var n=new Native();
 var runner=MakeScan(n,new(),()=>w);p.coverScans.Add(new(),new(){Runner=runner});runner.MoveNext();Keep.Scan=runner;
 var refs=new[]{new WeakReference(w),new WeakReference(b),new WeakReference(n)};p.End();return refs;}
sealed class Native:IEnumerator,IDisposable {public bool Fail;public int Disposed;public object Current=>this;public bool MoveNext()=>true;public void Reset(){}public void Dispose(){Disposed++;if(Fail)throw new Exception("native iterator failed");}}

static class Keep {internal static CoverScanRunner<object>? Scan;}
