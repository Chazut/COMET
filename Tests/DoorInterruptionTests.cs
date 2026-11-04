using Comet.Core;
using Comet.Server;
using System.Text.Json;

internal static class DoorInterruptionTests
{
    internal static int Run()
    {
        int n=0;void Check(bool ok,string why){if(!ok)throw new Exception("D12: "+why);n++;}
        void Invalid(Action a){try{a();}catch(ArgumentException){n++;return;}throw new Exception("D12 invalid config accepted");}
        var c=new DoorInterruptionConfig();c.Validate();
        Check(c.Enabled&&c.ResumeWindow==15&&c.QuietPeriod==3&&c.HeardDistance==25&&c.HeardAge==3&&c.GunfireDistance==12&&c.GunfireAge==2&&c.MaxResumes==1,"approved defaults");
        Check(JsonSerializer.Deserialize<CometConfig>("{}")!.DoorInterruption.ResumeWindow==15,"existing config default migration");
        foreach(var field in typeof(DoorInterruptionConfig).GetProperties().Where(x=>x.PropertyType==typeof(double)))
        foreach(var bad in new[]{-1d,double.NaN,double.PositiveInfinity,double.NegativeInfinity,1000})
        {var invalid=new DoorInterruptionConfig();field.SetValue(invalid,bad);Invalid(invalid.Validate);}
        Invalid(()=>new DoorInterruptionConfig{MaxResumes=2}.Validate());Invalid(()=>new DoorInterruptionConfig{MaxResumes=-1}.Validate());
        var dir=Path.Combine(Path.GetTempPath(),"comet-d12-"+Guid.NewGuid().ToString("N"));
        try
        {
            var store=new ConfigStore(Path.Combine(dir,"config.json"));var cfg=store.Read();cfg.DoorInterruption.Enabled=false;cfg.DoorInterruption.QuietPeriod=4;
            var saved=store.Save(cfg,cfg.Revision);var reload=store.Read();Check(!reload.DoorInterruption.Enabled&&reload.DoorInterruption.QuietPeriod==4,"server persistence");
            saved.DoorInterruption.ResumeWindow=99;Invalid(()=>store.Save(saved,saved.Revision));Check(store.Read().DoorInterruption.ResumeWindow==15,"invalid save atomic");
        }finally{Directory.Delete(dir,true);}
        string Reason(DoorThreat t)=>DoorInterruptionThreat.Reason(t,c);
        Check(Reason(new(){Visible=true,Source="seen"})=="other-visible-enemy","personal vision interrupts");
        Check(Reason(new(){Visible=true,Source="shared-seen"})=="","squad information not personal vision");
        Check(Reason(new(){Source="heard",Age=3,Distance=25})=="nearby-heard-enemy","heard boundaries inclusive");
        Check(Reason(new(){Source="heard",Age=3.01,Distance=2})=="","old hearing ignored");
        Check(Reason(new(){Source="heard",Age=0,Distance=25.01})=="","distant hearing ignored");
        Check(Reason(new(){Source="heard",Age=0,Distance=2,OtherFloor=true})=="","credible floor prevents false flank");
        Check(Reason(new(){Source="heard",Age=0,Distance=2,InTargetRoom=true})=="","target-room footsteps are not flank");
        Check(Reason(new(){Source="heard",Age=2,Distance=12,Gunfire=true,InTargetRoom=true,OtherFloor=true})=="nearby-gunfire","close gunfire retains priority");
        Check(Reason(new(){Source="shared-heard",Age=0,Distance=2,Gunfire=true})=="","shared signal cannot impersonate hearing");
        var floors=new DoorFloorEvidence();var listener=new DoorPoint(0,1,0);var heard=new DoorPoint(0,5,4);
        Check(floors.OtherFloor("e",100,100,listener,heard,true,3),"verified P04 floor remembered for dated sound");
        Check(floors.OtherFloor("e",102,100,listener,heard,false,3),"same sound does not become a flank after 0.25-second geometry cache expires");
        Check(!floors.OtherFloor("e",103.1,100,listener,heard,false,3),"floor evidence expires with original sound");
        Check(!floors.OtherFloor("e",101,101,listener,heard,false,3),"new sound requires new evidence");
        Check(!floors.OtherFloor("e",101,100,new(1,1,0),heard,false,3),"listener movement invalidates stored floor geometry");
        Check(!floors.OtherFloor("e",99,100,listener,heard,false,3),"future cue refused");
        for(int i=0;i<40;i++)floors.OtherFloor("e"+i,100,100,listener,heard,true,3);
        Check(floors.Count==32,"floor evidence bounded");floors.Clear();Check(floors.Count==0,"floor evidence cleanup");
        foreach(var age in new[]{-1d,double.NaN,double.PositiveInfinity})Check(Reason(new(){Source="heard",Age=age,Distance=2})=="","invalid hearing age refused");
        c.Enabled=false;Check(Reason(new(){Visible=true,Source="seen"})!="","disable resume does not disable interruptions");c.Enabled=true;
        DoorInterruptionTrace Ticket(string id="attempt")=>new(){Attempt=id,Plan="D09",Phase="holding",Target="original",Door="door",SafeStage=true,FeintConsumed=true};
        DoorInterruption Start(double until=120,DoorInterruptionConfig? cfg=null){var p=new DoorInterruption();p.Interrupt(Ticket(),100,until,"incoming-fire","","unlocated-impact",cfg??c);return p;}
        var p=Start();Check(p.Pending&&p.Deadline==115,"window capped by original deadline");
        Check(!p.Ready(102,"original",true,c)&&p.Trace!.Reason=="waiting-for-quiet","quiet period");
        Check(!p.Ready(103,"other",true,c)&&p.Trace!.Reason=="waiting-for-normal-target-selection","no forced target switch");
        Check(!p.Ready(103,"original",false,c),"native priority still wins");
        p.Threat(104,"nearby-gunfire","heard-id","heard");Check(!p.Ready(106.9,"original",true,c),"renewed threat delays quiet only");
        Check(p.Ready(107,"original",true,c),"safe return allowed");p.Request();Check(!p.Execute(107,c),"request is not acceptance");
        p.Accept(true);Check(p.Execute(107.1,c)&&p.Resumed&&p.Trace!.Resumes==1&&p.Trace.FeintConsumed,"execution preserves consumed action");
        Check(p.Deadline==115&&p.Trace!.Remaining<8,"no budget refund");
        Check(!p.Interrupt(Ticket(),108,200,"incoming-fire","","unlocated-impact",c)&&p.Trace!.Reason=="resume-limit-reached","second interruption abandons same attempt");
        Check(p.Deadline==115,"second interruption cannot refresh original deadline");
        p=Start();p.Ready(103,"original",true,c);p.Request();p.Accept(true);p.Execute(103,c);
        var continued=Ticket();continued.Plan="D07";continued.Phase="entry-ready";
        Check(!p.Interrupt(continued,104,200,"incoming-fire","","unlocated-impact",c)&&p.Trace!.Reason=="resume-limit-reached","phase continuation retains the same attempt's single resume");
        var handoff=new SainHandoff(new HandoffConfig());var cue=new HandoffCue{Target="original",At=99};
        handoff.Accepted(99,"A01","Search",cue);handoff.Begin(100,"new-threat","priority",cue);
        Check(handoff.Blocked(100,cue),"normal native handoff retry still applies before resume");
        Check(!handoff.Blocked(103,cue),"quiet interval allows ordinary handoff retry");
        handoff.Accepted(103,"A01","Search",cue);Check(!handoff.Blocked(103,cue),"accepted resume does not block its own action callback");
        Check(p.Interrupt(Ticket("new-attempt"),200,210,"incoming-fire","","unlocated-impact",c),"independent later attempt allowed");
        p=Start(102);Check(!p.Tick(102,c)&&p.Trace!.Reason=="original-budget-expired","original limit can expire before quiet");
        p=Start();Check(!p.Tick(99,c)&&p.Trace!.Reason=="invalid-clock","backward clock fails closed");
        p=Start();Check(!p.Tick(double.NaN,c),"nonfinite clock fails closed");
        p=Start();p.Ready(103,"original",true,c);p.Request();p.Accept(false);Check(!p.Pending&&!p.Resumed,"native refusal ends ticket");
        p=Start();p.Ready(103,"original",true,c);p.Request();p.Accept(true);p.Threat(103.1,"nearby-heard-enemy","e","heard");
        Check(p.Trace!.State=="waiting"&&!p.Trace.NativeAccepted&&!p.Execute(103.1,c),"late threat revokes pending native acceptance");
        p=Start(cfg:new(){Enabled=false});Check(!p.Pending&&p.Trace!.Reason=="resume-disabled","disabled still records interruption");
        p=Start(cfg:new(){MaxResumes=0});Check(!p.Pending,"zero resumes disables resume");
        p=new();var committed=Ticket();committed.SafeStage=false;committed.GrenadeConsumed=true;
        p.Interrupt(committed,100,120,"nearby-gunfire","e","heard",c);Check(!p.Pending&&p.Trace!.GrenadeConsumed&&p.Trace.Reason=="committed-action-not-replayed","committed throw not replayed or refunded");
        // D01 retains its original movement deadline across the real policy's pause/resume methods.
        var approach=new DoorApproach();var point=new DoorCandidate{Door="door",Side="left",X=1,Y=0,Z=0,Eligible=true};
        approach.Begin("original","heard",new(0,0,5),1,100);approach.Request(point,new(0,0,0),100);approach.Accept(true,100);
        approach.Suspend();Check(!approach.Active&&!approach.Planning&&approach.Deadline(12)==112,"D01 suspended without resetting clock");
        Check(approach.Resume(new(0,0,-1),104,12),"D01 safe resume");
        approach.Observe("original",new(0,0,5),new(0,0,0),6,105,12,true,false);Check(approach.Trace!.Remaining==7,"D01 original timer after resume");
        approach.Suspend();Check(!approach.Resume(new(0,0,0),112,12),"D01 expired route not revived");
        var ambush=new DoorAmbush();var ac=new DoorAmbushConfig();
        Check(ambush.Begin("original","heard","Normal",new(0,0,5),1,100,.5,25,0,20,ac),"D09 start");
        ambush.Prepare(point,new(0,1,0),.7,"Open");ambush.Request(new(0,0,0),true);ambush.Movement(new(1,0,0),true,true,101);ambush.Angle(true,true,101);
        var duration=ambush.Trace!.WaitDuration;var deadline=ambush.Deadline;ambush.Trace.Feint="medical";ambush.Suspend();
        Check(ambush.Resume(new(0,0,0),105,12),"D09 safe resume");ambush.Movement(new(1,0,0),true,true,106);
        ambush.Observe(106,7,true,new(0,0,5),0,20,12);
        Check(ambush.Trace.WaitDuration==duration&&ambush.Trace.HoldElapsed==5&&ambush.Deadline==deadline&&ambush.Trace.Feint=="medical","D09 wait and consumed feint unchanged");
        ambush.Suspend();ambush.AbandonSuspended("test");Check(!ambush.CanBegin("original",8,107,ac),"D09 abandonment keeps cooldown history");
        var clear=new DoorRoomClear();var cc=new DoorRoomClearConfig();
        Check(clear.Begin("door","original","heard","Chad",new(0,0,5),1,100,0,cc),"D07 start");clear.Opened(100.1);clear.Ready(100.2);
        clear.Choose(true,false,"","disabled",.75,.5,100.3,cc);var roll=clear.Trace!.Roll;var variant=clear.Trace.VariantRoll;
        Check(clear.SafeToSuspend,"D07 safe precommit phase");clear.Suspend();Check(!clear.Active&&clear.Resume(101),"D07 short configured quiet allows resume");
        Check(clear.Trace.Roll==roll&&clear.Trace.VariantRoll==variant&&clear.Trace.State=="entry-ready","D07 no chance or variant reroll");
        clear.Suspend();Check(!clear.Resume(104),"D07 original 3-second preparation deadline wins over resume window");
        clear.End("test");Check(!clear.Begin("door","original","heard","Chad",new(0,0,5),1,105,0,cc),"D07 cooldown not refunded");
        var board=new DoorSupportBoard();var sc=new DoorSupportConfig();board.Begin("op","leader","door","original",100,12,sc);
        var offer=board.Offer("op","one","overwatch",new(3,0,0),new(0,1,0),0,1,100,sc)!;board.Accept("one",100);
        board.End("one","interrupted");Check(!board.CanOffer("op","overwatch","one",104),"support reservation released but history kept");
        var next=board.Offer("op","two","overwatch",new(3,0,0),new(0,1,0),0,1,104,sc)!;
        Check(next.Remaining==8,"support resume cannot reset original budget");board.End("two","interrupted");
        Check(!board.CanOffer("op","overwatch","three",105),"support resume cannot reset replacement count");
        // Multiple deadlines and interruption times: no ready/executed ticket can outlive the earliest cap.
        for(int i=1;i<=100;i++)
        {
            double until=100+i*.2;var f=Start(until);double end=Math.Min(until,115);
            Check(f.Deadline==end,"fixed minimum deadline");f.Ready(end,"original",true,c);Check(!f.Pending&&!f.Resumed,"expired at exact deadline");
        }
        p=Start();p.Ready(103,"original",true,c);p.Trace!.Gates=new[]{"original contact valid","same door","complete own-side route","protected point","allies clear","weapon and resources ready"};
        p.Request();p.Accept(true);p.Execute(103.1,c);
        var json=JsonSerializer.Serialize(new{schema=1,botInstance="instance",doorInterruption=p.Trace,doorInterruptionConfig=c},new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase,WriteIndented=true});
        Directory.CreateDirectory("artifacts");File.WriteAllText("artifacts/d12-door-interruption.json",json);
        Console.WriteLine($"D12: {n} checks passed (policy, original deadlines, resource history, config persistence)");return n;
    }
}
