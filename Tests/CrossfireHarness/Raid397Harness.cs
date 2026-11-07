using Comet.Client;
using Comet.Core;
using EFT;
using SAIN;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
using UnityEngine.AI;

internal static class Raid397Harness
{
    internal static void Run()
    {
        int checks=0;
        void Check(bool ok,string why){checks++;if(!ok)throw new Exception("raid397: "+why);}
        var c=new SquadManeuverConfig{Chance=100};var board=new SquadManeuverBoard();
        var willing=new ManeuverMember{Id="a",Name="Only willing",Score=1,Ammo=70,Roll=.1};
        var other=new ManeuverMember{Id="b",Name="Declines",Score=2,Ammo=70,Roll=.9};
        var plan=board.TryStart("g","e",10,10,.1,new(){willing,other},c,70);
        Check(plan?.Support=="b"&&willing.Role=="flank","do not spend the only willing flanker as support");
        Check(other.SelectionReason=="individual-participation-declined","personal reason is preserved");
        board.Finish(plan!,11,c,"contact-ended");
        Check(board.TryStart("g","e",12,12,.1,new(){willing,other},c,70)==null&&board.LastReason=="maneuver-retry-cooldown","current cooldown cannot repeat previous result");
        board.Clear();willing.Ammo=13d/30*100;other.Score=-10;
        Check(board.TryStart("g","e",10,10,.1,new(){willing,other},c,70)==null,"low-ammo cautious duo may legitimately decline");
        var detail=board.LastAttempt!.Trace("a",10,c);
        Check(detail.Selection[0].Reason=="personality-holds-support"&&detail.Selection[1].Reason=="ammo-below-flank-threshold","both member causes survive plan termination");
        Check(detail.Selection[1].Ammo<50&&detail.Selection[1].MinimumAmmo==50&&detail.Selection[1].Roll==10,"telemetry carries actual thresholds and rolls");
        Check(board.TryStart("g2","e",11,11,.1,new(){willing},c,70)==null&&board.LastReason=="maneuver-insufficient-eligible-members","current membership failure is explicit");

        (Plugin,BotComponent,BotPathDataManual) Setup()
        {
            Time.time=Time.realtimeSinceStartup=10;Time.frameCount=0;Physics.Blocked=false;Physics.Cast=null;
            NavMesh.Valid=true;NavMesh.Sample=null;NavMesh.Route=null;SAINEnableClass.Bots.Clear();Api.Decisions.Labels.Clear();
            var p=new Plugin();p.config!.Crossfire.Participation=100;
            var b=new BotComponent{Position=new(-3,0,0)};var support=new BotComponent();support.GoalEnemy!.IsVisible=true;
            var members=new Dictionary<string,BotComponent>{{b.ProfileId,b},{support.ProfileId,support}};
            foreach(var x in members.Values){x.Squad.Members=members;x.EnemyController.KnownEnemies.Add(x.GoalEnemy!);SAINEnableClass.Bots[x.ProfileId]=x;}
            b.Decision.CurrentCombatDecision=ECombatDecision.SeekCover;
            var path=new BotPathDataManual{TimeStarted=10,Destination=new(0,0,20),OnStop=()=>b.Mover.ActivePath=null};b.Mover.ActivePath=path;
            p.Observe(b);return(p,b,path);
        }
        {
            var(p,b,path)=Setup();var r=p.states[b].Crossfire;
            Check(r.Policy.Trace?.State=="planning"&&path.Stops==0,"preparing alone does not cancel native path");
            Check(p.Choose(b),"plan can request the tactical layer while preparing");
            var layer=new CrossfireLayer(b.BotOwner,21);Check(layer.IsActive(),"planning layer can prevent origin drift");
            var action=new CrossfireAction(b.BotOwner);action.Start();
            Check(path.Stops==1&&r.Layer&&r.Policy.Active&&r.Policy.Trace?.State=="planning","selected planning action takes only its observed route");
            Check(!r.Policy.Trace!.Applied,"planning is not reported as executed flanking");
            b.BotOwner.Memory.IsUnderFire=true;p.Sample(b);
            Check(!r.Policy.Active,"real danger interrupts even during preparation");
        }
        {
            var(p,b,path)=Setup();b.Player.MovementContext.IsGrounded=false;Time.time+=.1f;p.Sample(b);
            Check(p.states[b].Crossfire.Policy.Active&&path.Stops==0,"one short airborne step retains preparation without cancelling a route");
            Time.time+=.61f;p.Sample(b);
            Check(!p.states[b].Crossfire.Policy.Active&&p.states[b].Crossfire.Policy.Trace?.Reason=="crossfire-not-grounded","sustained airborne state still aborts");
        }
        {
            var(p,b,path)=Setup();var r=p.states[b].Crossfire;
            var mate=b.Squad.Members.Values.First(x=>!ReferenceEquals(x,b));
            var board2=new SquadManeuverBoard();var plan2=board2.TryStart("g2","e",10,10,0,
                new(){new(){Id=b.ProfileId,Score=2,Ammo=100,Roll=0},new(){Id=mate.ProfileId,Score=-1,Ammo=100,Roll=0}},c,100)!;
            r.Maneuver=plan2;r.ManeuverRole=plan2.Member(b.ProfileId);p.Raid397Record(b,true);
            var snapshot=r.Policy.Trace!.Maneuver;int logs=p.diagnostics!.Lines.Count;
            long before=GC.GetAllocatedBytesForCurrentThread();
            for(int i=0;i<1000;i++)p.Raid397Record(b);
            long bytes=GC.GetAllocatedBytesForCurrentThread()-before;
            Check(ReferenceEquals(snapshot,r.Policy.Trace.Maneuver)&&p.diagnostics.Lines.Count==logs&&bytes==0,
                "same-frame detailed telemetry performs no member snapshot allocation or duplicate write");
            r.ManeuverRole!.Confirmed=true;Time.time+=.11f;p.Raid397Record(b);
            Check(!ReferenceEquals(snapshot,r.Policy.Trace.Maneuver)&&r.Policy.Trace.Maneuver!.Confirmed,
                "next telemetry sample reflects live member progress");
            Time.time+=.01f;p.End(b);
            Check(r.Policy.Trace.State=="ended"&&p.diagnostics.Lines.Count>logs,
                "terminal evidence bypasses snapshot throttling");
        }
        Console.WriteLine($"{checks} raid397 squad regression checks passed");
    }
}

namespace Comet.Client
{
    public sealed partial class Plugin
    {
        internal void Raid397Record(BotComponent bot,bool force=false)=>RecordCrossfire(states[bot],force);
    }
}
