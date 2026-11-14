using Comet.Client;
using EFT;
using HarmonyLib;
using SAIN;
using SAIN.Components;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SAIN.SAINComponent.Classes.Mover;
using SAIN.SAINComponent.Classes.WeaponFunction;
using SAIN.Types.Jobs;
using UnityEngine;

Plugin.Patch();
int checks=0;
void Check(bool condition,string message) { checks++;if(!condition)throw new Exception(message); }
(Plugin p,BotComponent b) Setup(string mode="solo")
{
    Time.time=Time.realtimeSinceStartup=0;Physics.Wall=float.PositiveInfinity;Physics.Calls=0;
    var p=new Plugin();var b=new BotComponent();SAINEnableClass.Bots.Add(b.ProfileId,b);
    if(mode=="off")p.config!.Enabled=false;
    if(mode=="client")p.Authority=false;
    if(mode=="scav")b.Supported=false;
    if(mode=="ghost")p.IsGhost=true;
    if(mode=="dead")b.IsDead=true;
    if(mode=="inactive")b.BotActive=false;
    if(mode=="ending")b.GameEnding=true;
    return(p,b);
}
foreach(string mode in new[]{"solo","host","off","client","scav","ghost","dead","inactive","ending"})
{
    var(p,b)=Setup(mode);bool scoped=mode is "solo" or "host";
    var enemy=new Enemy(b);var nodes=new[]{new BotVisiblePathNode(new(10,0,0))};
    var query=new QueryParameters{layerMask=123,hitTriggers=7};
    foreach(var job in new[]{new PathVisionJob(nodes,1,default,enemy,query),new PathVisionJob(nodes.ToList(),default,enemy,query)})
    {
        Check(job.Commands[0].distance==(scoped?10:1),mode+": both ray overloads and scope");
        Check(job.Commands[0].direction.x==(scoped?1:10),mode+": ray direction");
        Check(job.Commands[0].queryParameters.layerMask==123&&job.Commands[0].queryParameters.hitTriggers==7,"query preserved");
    }
    var path=new SAINEnemyPath(b);Check(path.ShallCalcNewPath(1),"initial path");
    path.Target=new(10,0,0);Check(!path.ShallCalcNewPath(1.2f),"native delay retained");
    Check(path.ShallCalcNewPath(2.1f)==scoped,mode+": movement during delay is not consumed");
    path.Clear();Check(path.ShallCalcNewPath(3.2f)==scoped,mode+": Clear invalidates position cache");
    enemy.EnemyPlayerComponent.IsActive=false;
    Check(Enemy.IsEnemyActive(enemy)==!scoped,mode+": target activity instead of observer activity");
    Check(Enemy.IsEnemyValid(b.ProfileId,b.PlayerComponent)==!scoped,mode+": existing native self check reachable");
    var recoil=new Recoil(b);recoil.WeaponShot(-1,-1);Time.time=.02f;recoil.CalcRecoilDecay();
    Check(recoil.Finished==!scoped,mode+": signed negative recoil");
    var pose=new PoseClass(b);pose.ManualUpdate();Time.time+=.1f;pose.ManualUpdate();
    Check(Math.Abs(pose.Elapsed-(scoped?.1f:.04f))<.00001,mode+": pose clock scope");
    p.Clear();Check(p.Counts.All(x=>x==0),"raid counters cleared");
}
{
    var(p,b)=Setup();var e=new Enemy(b);
    e.EnemyPlayerComponent.BotOwner!.StandBy.StandByType=BotStandByType.sleep;
    Check(!Enemy.IsEnemyActive(e),"target standby still rejected");
    e.EnemyPlayerComponent.BotOwner=null;Check(!Enemy.IsEnemyActive(e),"missing target owner still rejected");
    e.EnemyPlayerComponent.IsAI=false;e.EnemyPlayerComponent.IsActive=true;
    Check(Enemy.IsEnemyActive(e),"active human accepted without bot owner");
    e.EnemyPlayerComponent.Alive=false;Check(!Enemy.IsEnemyValid(b.ProfileId,e.EnemyPlayerComponent),"native health check retained");
    var path=new SAINEnemyPath(b){Known=false};Check(!path.ShallCalcNewPath(20),"unknown path rejected natively");
    path.Known=true;path.InRange=false;Check(!path.ShallCalcNewPath(20),"native path range retained");
}
foreach(bool failure in new[]{false,true})
{
    var(p,b)=Setup();var normal=new BotComponent("scav"){Supported=false};
    var first=new GrenadeReactionClass(b);var second=new GrenadeReactionClass(new("pmc2"));var excluded=new GrenadeReactionClass(normal);
    var controller=new GrenadeController{ThrowAfter=failure};controller.Observers.AddRange(new[]{first,excluded,second});
    Physics.Wall=7;try{controller.Throw(new());Check(!failure,"expected native throw failure");}catch(InvalidOperationException){Check(failure,"native failure preserved");}
    Check(first.Point.x==7&&second.Point.x==7&&excluded.Point.x==10,"second-segment wall and per-observer scope");
    Check(Physics.Calls==4,"corrected grenade prediction computed only once across supported observers");
    Check(!p.ContextActive,"prediction context restored on success and exception");
    Check(SAIN.Helpers.Vector.DangerPoint(default,new(10,0,0),1).x==10,"unrelated/native prediction unchanged");
    first.EnemyGrenadeThrown(new(),new(9,0,0),"other");Check(first.Point.x==9,"no stale throw context leaks into later events");
}
{
    var(p,b)=Setup();var outer=new GrenadeController();var inner=new GrenadeController();
    var a=new GrenadeReactionClass(b);var c=new GrenadeReactionClass(b);outer.Observers.Add(a);inner.Observers.Add(c);
    outer.Nested=()=>inner.Throw(new());Physics.Wall=7;outer.Throw(new());
    Check(a.Point.x==7&&c.Point.x==7&&!p.ContextActive,"nested throw restores outer context");
}
foreach(int rate in new[]{30,60,144})
{
    var(p,b)=Setup();var r=new Recoil(b);r.WeaponShot(-1,-1);
    var pose=new PoseClass(b);var lean=new LeanClass(b);pose.ManualUpdate();lean.Tick();
    for(int i=1;i<=rate;i++){Time.time=(float)i/rate;r.CalcRecoilDecay();pose.ManualUpdate();lean.Reset=i%2==0;lean.Tick();}
    float expected=(float)Math.Pow(1-.02f*.3f,50);
    Check(Math.Abs(r.H+expected)<.00001&&Math.Abs(r.V+expected)<.00001,rate+": recoil independent of callback rate");
    Check(Math.Abs(pose.Elapsed-1)<.00001&&Math.Abs(lean.Elapsed-1)<.00001,rate+": both smoothing clocks use elapsed seconds");
    r.WeaponShot(1,1);r.CalcRecoilDecay();Check(r.H==1,"same-frame shot has zero elapsed decay");
    pose.ManualUpdate();lean.Tick();Check(Math.Abs(pose.Elapsed-1)<.00001&&Math.Abs(lean.Elapsed-1)<.00001,"same-frame smooth calls do not double step");
    Time.time+=3;pose.ManualUpdate();Check(Math.Abs(pose.Elapsed-1.25)<.00001,"liquidwarp smoothing resume cap retained");
}
{
    var(p,b)=Setup();var r=new Recoil(b);r.WeaponShot(.0001f,-.0001f);r.CalcRecoilDecay();
    Check(r.Finished&&r.H==0&&r.V==0,"small signed recoil settles exactly");
    b.IsCheater=true;r.WeaponShot(-1,-1);r.CalcRecoilDecay();Check(r.Finished,"cheater follows native path");
}
// Model a successful earlier W09 prefix. Harmony must skip the native fix to
// avoid double decay; actual W09 policies are exercised by RecoilHarness.
new Harmony("w09-order-fixture").Patch(AccessTools.Method(typeof(Recoil),nameof(Recoil.CalcRecoilDecay)),
    prefix:new HarmonyMethod(typeof(OptionalRecovery),nameof(OptionalRecovery.Apply)));
{
    var(p,b)=Setup();var r=new Recoil(b);r.WeaponShot(1,1);OptionalRecovery.Active=true;Time.time=.02f;r.CalcRecoilDecay();
    Check(r.H==.5f&&r.V==.5f,"earlier W09 replacement is not decayed a second time");
    OptionalRecovery.Active=false;Time.time=.04f;r.CalcRecoilDecay();
    Check(Math.Abs(r.H-.5f*(1-.02f*.3f))<.00001,"baseline resumes with only the latest elapsed step");
    p.diagnostics.Quiet=true;int rr=Api.Decisions.Lines.Count;p.Flush();
    Check(Api.Decisions.Lines.Count==rr+1&&p.diagnostics.Lines.Count==0,"quiet keeps RR summaries");
    Time.time=.06f;r.CalcRecoilDecay();p.Flush();Check(Api.Decisions.Lines.Count==rr+1,"summary rate limited");
    p.Clear();Check(Api.Decisions.Lines.Count==rr+2&&p.diagnostics.Lines.Count==1,"raid end flushes unsent final counters");
}
Console.WriteLine($"Native fixes: {checks} checks passed (production Harmony patches, native-shaped fixtures).");

static class OptionalRecovery
{
    internal static bool Active;
    public static bool Apply(ref float ____currentRecoilHorizAngle,ref float ____currentRecoilVertAngle)
    {
        if(!Active)return true;
        ____currentRecoilHorizAngle*=.5f;____currentRecoilVertAngle*=.5f;return false;
    }
}
