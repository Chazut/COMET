using Comet.Core;
using Comet.Server;
using System.Text.Json;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

internal static class CombatStepTests
{
    public static int Run()
    {
        int n=0;
        void Check(bool ok,string why){if(!ok)throw new Exception("M01: "+why);n++;}
        void Invalid(Action action,string why){try{action();}catch(ArgumentException){n++;return;}throw new Exception("M01 accepted "+why);}
        var c=new CombatStepConfig();c.Validate();
        CombatStepFrame Frame(double now=10)=>new(){Now=now,Target="known",Distance=12,Position=new(0,0,0),Known=new(0,0,12)};
        CombatStep Start(double roll=0){var x=new CombatStep();Check(x.Begin(Frame(),c,roll,0,0),"begin eligible sequence");return x;}
        Check(c.MinDistance==3&&c.MaxDistance==25&&c.Radius==1.5&&c.StepMin==.35&&c.StepMax==.7&&c.SequenceMin==1.5&&c.SequenceMax==3&&c.CooldownMin==2&&c.CooldownMax==4,"approved defaults");
        foreach(var mutate in new Action<CombatStepFrame>[] {
            f=>f.Pmc=false,f=>f.Authority=false,f=>f.Active=false,f=>f.Ghost=true,f=>f.Enabled=false,f=>f.Visible=false,f=>f.Target="",
            f=>f.Action="RushEnemy",f=>f.Action="Search",f=>f.Action="Retreat",f=>f.Source="heard",f=>f.Source="shared-seen",
            f=>f.Urgent=true,f=>f.Busy=true,f=>f.Ready=false,f=>f.Grounded=false,f=>f.Immobile=true,f=>f.Sprinting=true,f=>f.ProtectedCover=true,
            f=>f.Distance=2.99,f=>f.Distance=25.01,f=>f.ContactAge=-1,f=>f.Distance=double.NaN,f=>f.Position=new(double.NaN,0,0)
        }) {
            var f=Frame();mutate(f);var blocked=new CombatStep();Check(!blocked.Begin(f,c,0,0,0),"ineligible cannot start");
            var active=Start();var changed=Frame(10.1);mutate(changed);Check(!active.Observe(changed,c)&&!active.Active,"same gate revokes existing sequence");
        }
        var a=Start();Check(!a.Begin(Frame(10.01),c,0,0,0),"no reroll during active sequence");
        Check(a.Request(new(.8,0,0),"right",10,0,c),"native request recorded");
        Check(a.Trace!.Requested&&!a.Trace.MovementObserved&&a.Trace.ActualDistance==0,"request does not prove travel");
        var replaced=Start();replaced.Request(new(.8,0,0),"right",10,0,c);
        var foreignMovement=Frame(10.1);foreignMovement.Position=new(.2,0,0);foreignMovement.NativeRouteValid=false;
        Check(!replaced.Observe(foreignMovement,c)&&replaced.Trace!.Reason=="movement-route-replaced"
            &&replaced.Trace.ActualDistance==0&&!replaced.Trace.MovementObserved,"foreign native route never contributes M01 travel");
        Check(!a.Request(new(-.8,0,0),"left",10.1,0,c),"minimum step duration prevents command spam");
        var f1=Frame(10.1);f1.Position=new(.15,0,0);Check(a.Observe(f1,c)&&a.Trace.MovementObserved&&Math.Abs(a.Trace.ActualDistance-.15)<.0001,"actual position proves travel");
        f1=Frame(10.2);f1.Position=new(.15,0,0);f1.Action="DogFight";Check(a.Observe(f1,c)&&a.Trace.Requests==1,"compatible action context does not reset roll");
        Check(!a.Request(new(1.51,0,0),"right",10.4,0,c),"target outside radius rejected");
        Check(!a.Request(new(0,.31,0),"upstairs",10.4,0,c),"different floor rejected");
        Check(a.Request(new(-.4,0,0),"left",10.4,.999,c)&&a.StepDeadline>11.09&&a.StepDeadline<11.1,"maximum held direction");
        for(int i=3;i<=15;i++){var f=Frame(10+i*.1);f.Position=new(.15+i*.02,0,0);a.Observe(f,c);}
        Check(!a.Active&&a.Trace.Reason=="movement-sequence-finished"&&a.Trace.ValidFor==0,"sequence completes by clock, not by claimed hits");
        Check(!a.Begin(Frame(12),c,0,0,0),"cooldown starts at actual end");
        Check(a.Begin(Frame(13.6),c,0,0,0),"new opportunity after cooldown");
        a=Start();a.End("native-action-ended",10.05);
        Check(!a.Begin(Frame(10.06),c,0,0,0)&&!a.Begin(new(){Now=10.07,Target="other",Distance=12},c,0,0,0),"action or target churn cannot bypass cooldown");
        var declined=new CombatStep();Check(!declined.Begin(Frame(),c,.99,0,0)&&declined.Trace!.Rolled&&!declined.Trace.Requested,"declined opportunity");
        Check(!declined.Begin(Frame(10.2),c,0,0,0),"no per-frame retry after decline");
        foreach(var personality in c.Chances.Keys){var f=Frame();f.Personality=personality;Check(new CombatStep().Begin(f,c,0,0,0),"all known personalities configurable");}
        var unknown=Frame();unknown.Personality="unknown";Check(!new CombatStep().Begin(unknown,c,0,0,0),"unknown personality does not silently opt in");
        c.Chances["Normal"]=0;Check(!new CombatStep().Begin(Frame(),c,0,0,0),"zero disables personality");c=new();
        foreach(double invalid in new[]{-1d,1d,double.NaN,double.PositiveInfinity})Check(!new CombatStep().Begin(Frame(),c,invalid,0,0),"invalid random sample");
        a=Start();var target=Frame(10.1);target.Target="different";Check(!a.Observe(target,c)&&a.Trace!.Reason=="movement-contact-replaced","target identity change");
        a=Start();Check(!a.Observe(Frame(9.9),c),"clock reversal");a=Start();Check(!a.Observe(Frame(10.51),c),"missed observations cannot prolong plan");
        a=Start();a.Request(new(.8,0,0),"right",10,0,c);var tele=Frame(10.01);tele.Position=new(1,0,0);Check(!a.Observe(tele,c)&&!a.Trace!.MovementObserved,"teleport is not successful movement");
        a=Start();a.Request(new(.8,0,0),"right",10,0,c);
        for(int i=1;i<=10;i++)a.Observe(Frame(10+i*.1),c);
        Check(!a.Active&&a.Trace!.Reason=="movement-stalled"&&!a.Trace.MovementObserved,"stalled actor ends without false progress");
        a=Start();for(int i=1;i<=6;i++)a.Observe(Frame(10+i*.1),c);Check(!a.Active&&a.Trace!.Reason=="movement-planning-timeout","planning cannot wait indefinitely");
        var origin=new DoorPoint(0,0,0);var end=new DoorPoint(.8,0,0);
        Check(CombatStep.Route(origin,origin,end,new[]{origin,end},true,1.5),"short straight route");
        Check(!CombatStep.Route(origin,origin,end,new[]{origin,new DoorPoint(0,0,1),end},true,1.5),"detour cannot masquerade as local step");
        Check(!CombatStep.Route(origin,origin,end,new[]{origin,end},false,1.5),"partial route rejected");
        Check(!CombatStep.Route(origin,origin,end,new[]{new DoorPoint(.3,0,0),end},true,1.5),"route from stale location rejected");
        Check(!CombatStep.Route(origin,origin,end,new[]{origin,new DoorPoint(.8,.4,0)},true,1.5),"height mismatch");
        Check(!CombatStep.Route(origin,origin,end,new[]{origin,new DoorPoint(double.NaN,0,0),end},true,1.5),"invalid route geometry");
        for(int before=0;before<=7;before++)for(int mid=0;mid<=7;mid++)for(int after=0;after<=7;after++)
            Check(CombatStep.PreservesProtection(before,mid,after)==((mid&before)==before&&(after&before)==before),"each protected body sample stays protected");
        foreach(var property in typeof(CombatStepConfig).GetProperties().Where(x=>x.PropertyType==typeof(double))) {
            foreach(var invalid in new[]{double.NaN,double.PositiveInfinity,-1d,1000d}){var config=new CombatStepConfig();property.SetValue(config,invalid);Invalid(config.Validate,"invalid "+property.Name);}
        }
        Invalid(()=>new CombatStepConfig{StepMin=.7,StepMax=.35}.Validate(),"reversed step bounds");
        Invalid(()=>new CombatStepConfig{MinDistance=10,MaxDistance=5}.Validate(),"reversed range");
        Invalid(()=>new CombatStepConfig{Chances=null!}.Validate(),"null chances");
        var missing=new CombatStepConfig();missing.Chances.Remove("Rat");Invalid(missing.Validate,"missing personality");
        var bad=new CombatStepConfig();bad.Chances["Rat"]=101;Invalid(bad.Validate,"chance above 100");
        var legacy=JsonSerializer.Deserialize<CometConfig>("{}")!;legacy.Validate();Check(legacy.CombatStep.Radius==1.5,"legacy config gains defaults");
        Invalid(()=>JsonSerializer.Deserialize<CometConfig>("{\"CombatStep\":null}")!.Validate(),"explicit null config");
        var dir=Path.Combine(Path.GetTempPath(),"comet-m01-"+Guid.NewGuid().ToString("N"));var file=Path.Combine(dir,"config.json");
        try {
            var store=new ConfigStore(file);var config=store.Read();config.CombatStep.Radius=1.2;config.CombatStep.Chances["Rat"]=23;
            store.Save(config,config.Revision);Check(store.Read().CombatStep.Radius==1.2&&store.Read().CombatStep.Chances["Rat"]==23,"server round trip");
            var before=File.ReadAllText(file);config=store.Read();config.CombatStep.StepMin=2;
            Invalid(()=>store.Save(config,config.Revision),"invalid save");Check(File.ReadAllText(file)==before,"invalid save is atomic");
        } finally {foreach(var path in new[]{file,file+".bak"})if(File.Exists(path))File.Delete(path);if(Directory.Exists(dir))Directory.Delete(dir);}
        using(var pe=new PEReader(File.OpenRead("references/sain-4.5.1/SAIN.dll"))) {
            var md=pe.GetMetadataReader();TypeDefinition Type(string name)=>md.TypeDefinitions.Select(h=>md.GetTypeDefinition(h)).Single(t=>md.GetString(t.Name)==name);
            foreach(var name in new[]{"StandAndShootAction","DogFightAction"})foreach(var method in new[]{"Update","Stop"})
                Check(Type(name).GetMethods().Any(h=>md.GetString(md.GetMethodDefinition(h).Name)==method),"official SAIN action contract "+name+"."+method);
            var mover=Type("PlayerMovementController");Check(mover.GetFields().Any(h=>md.GetString(md.GetFieldDefinition(h).Name)=="_targetMoveSpeed"),"native speed restore contract");
            var pathType=Type("BotPathDataManual");Check(pathType.GetMethods().Any(h=>md.GetString(md.GetMethodDefinition(h).Name)=="Stop"),"native immediate completion instead of delayed Cancel");
            foreach(var field in new[]{"_preparedPath1","_preparedPath2"})Check(Type("SAINMoverClass").GetFields().Any(h=>md.GetString(md.GetFieldDefinition(h).Name)==field),"native queued-path guard "+field);
            var move=mover.GetMethods().Select(h=>md.GetMethodDefinition(h)).Single(m=>md.GetString(m.Name)=="SetTargetMoveDirection");
            var parameters=move.GetParameters().Select(h=>md.GetString(md.GetParameter(h).Name)).ToArray();Check(parameters.Contains("playerComp")&&parameters.Contains("START_SLOW_DIST"),"scoped arrival hook parameters");
        }
        a=Start();a.Request(end,"right",10,0,c);var observed=Frame(10.2);observed.Position=new(.18,0,0);a.Observe(observed,c);
        File.WriteAllText("artifacts/m01-combat-step.json",JsonSerializer.Serialize(new{combatStep=a.Trace},new JsonSerializerOptions{IncludeFields=true,PropertyNamingPolicy=JsonNamingPolicy.CamelCase,WriteIndented=true}));
        Console.WriteLine($"M01: {n} checks passed (scope, priorities, movement evidence, timing, geometry, configuration and native contracts)");return n;
    }
}
