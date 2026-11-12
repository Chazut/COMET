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
        Check(c.Enabled&&c.ResumeWindow==90&&c.QuietPeriod==3&&c.HeardDistance==25&&c.HeardAge==3&&c.GunfireDistance==12&&c.GunfireAge==2&&c.MaxResumes==1,"approved defaults");
        Check(JsonSerializer.Deserialize<CometConfig>("{}")!.DoorInterruption.ResumeWindow==90,"existing config default migration");
        foreach(var field in typeof(DoorInterruptionConfig).GetProperties().Where(x=>x.PropertyType==typeof(double)))
        foreach(var bad in new[]{-1d,double.NaN,double.PositiveInfinity,double.NegativeInfinity,1000})
        {var invalid=new DoorInterruptionConfig();field.SetValue(invalid,bad);Invalid(invalid.Validate);}
        Invalid(()=>new DoorInterruptionConfig{MaxResumes=2}.Validate());Invalid(()=>new DoorInterruptionConfig{MaxResumes=-1}.Validate());
        var dir=Path.Combine(Path.GetTempPath(),"comet-d12-"+Guid.NewGuid().ToString("N"));
        try
        {
            var store=new ConfigStore(Path.Combine(dir,"config.json"));var cfg=store.Read();cfg.DoorInterruption.Enabled=false;cfg.DoorInterruption.QuietPeriod=4;
            var saved=store.Save(cfg,cfg.Revision);var reload=store.Read();Check(!reload.DoorInterruption.Enabled&&reload.DoorInterruption.QuietPeriod==4,"server persistence");
            saved.DoorInterruption.ResumeWindow=999;Invalid(()=>store.Save(saved,saved.Revision));Check(store.Read().DoorInterruption.ResumeWindow==90,"invalid save atomic");
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
        var p=Start();Check(p.Pending&&p.Deadline==190,"source 90-second resume ticket independent of old action deadline");
        Check(!p.Ready(104.9,"original",true,c),"source five-second door cooldown");
        Check(!p.Ready(105,"other",true,c),"no forced target switch");
        Check(!p.Ready(105,"original",false,c),"active source priority blocks restart");
        p.Threat(106,"nearby-gunfire","heard-id","heard");
        Check(p.Ready(106,"original",true,c),"no additional quiet-period extension");
        p.Request();Check(!p.Execute(106,c),"request is not execution");
        p.Accept(true);Check(p.Execute(106.1,c)&&p.Resumed&&p.Deadline==256.1,"fresh source session budget after acceptance");
        Check(p.Interrupt(Ticket(),108,109,"incoming-fire","","heard",c)&&p.Deadline==198,"subsequent interruption gets a new source ticket");
        p=Start(102);Check(p.Ready(105,"original",true,c),"expired original action does not kill new session");
        p=Start();Check(!p.Tick(190,c)&&!p.Pending,"ticket expires after 90 seconds");
        p=Start();Check(!p.Tick(99,c),"backward clock cleanup");
        p=Start();Check(!p.Tick(double.NaN,c),"nonfinite clock cleanup");
        p=Start(cfg:new(){Enabled=false});Check(!p.Pending,"explicit feature disable");
        p=Start(cfg:new(){MaxResumes=0});Check(p.Pending,"retired one-resume cap is ignored");
        for(int i=1;i<=100;i++) {
            p=Start(100+i*.2);Check(p.Deadline==190,"old phase deadline cannot shorten source resume");
            Check(p.Ready(105,"original",true,c),"restart eligible after source cooldown");
            p.Request();p.Accept(false);Check(!p.Pending&&!p.Resumed,"native refusal does not fabricate execution");
        }
        p=Start();p.Ready(105,"original",true,c);p.Request();p.Accept(true);p.Execute(105.1,c);
        var json=JsonSerializer.Serialize(new{schema=1,botInstance="instance",doorInterruption=p.Trace,doorInterruptionConfig=c},new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase,WriteIndented=true});
        Directory.CreateDirectory("artifacts");File.WriteAllText("artifacts/d12-door-interruption.json",json);
        Console.WriteLine($"D12: {n} checks passed (source restart window, native execution, lifecycle, config persistence)");return n;
    }
}
