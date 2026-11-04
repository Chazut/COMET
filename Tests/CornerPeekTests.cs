using Comet.Core;
using Comet.Server;
using System.Text.Json;

internal static class CornerPeekTests
{
    public static int Run()
    {
        int checks=0;void Check(bool ok,string why){if(!ok)throw new Exception("M03: "+why);checks++;}
        void Invalid(Action fn){try{fn();}catch(ArgumentException){checks++;return;}throw new Exception("M03 accepted invalid configuration");}
        var c=new CornerPeekConfig();c.Validate();
        CombatStepFrame C(double now=10,double x=0,bool visible=true)=>new(){Now=Math.Round(now,6),Target="enemy",Position=new(x,0,0),Known=new(0,0,12),Distance=12,Visible=visible};
        CornerPeekFrame F(double now=10,double x=0,bool visible=true)=>new(){Combat=C(now,x,visible),Grounded=true,GeometryFresh=true,LaneSafe=true,Open=visible,Hidden=!visible,SinceHit=10,Speed=0};
        CornerPeek Begin(double roll=0,double style=.8,bool bold=true){var p=new CornerPeek();Check(p.Begin(C(),1,c,roll,style,0,0,bold),"selected valid opportunity");return p;}
        CornerPeek Planned(double style=.8){var p=Begin(0,style);Check(p.Plan(new(-.75,0,0),p.Trace!.Style,10.05),"validated geometry plan");return p;}
        CornerPeek Pending(double style=.8){var p=Planned(style);for(int i=1;i<=8;i++)p.Observe(F(10.05+i*.05),c,"");Check(p.WantsRoute,"exposure ends");return p;}
        CornerPeek Hidden(){var p=Pending();Check(p.Request(10.45),"retreat requested");
            for(int i=1;i<=4;i++){var f=F(10.45+i*.05,-.75*i/4,false);f.AtGoal=i==4;p.Observe(f,c,"");}
            Check(p.Trace!.State=="hidden"&&p.Trace.HiddenObserved,"actual hidden arrival");return p;}
        Check(c.Enabled&&c.Chance==50&&c.MinDistance==5&&c.MaxDistance==25&&c.MaxStep==1&&c.ExposedMin==.35&&c.ExposedMax==.6
            &&c.HiddenMin==.25&&c.HiddenMax==.45&&c.MaxPeeks==2&&c.SequenceLimit==4&&c.CooldownMin==4&&c.CooldownMax==6
            &&c.SightGrace==1.2&&c.JumpShare==25&&c.RunShare==15,"approved defaults");
        var p=new CornerPeek();Check(!p.Begin(C(),1,c,.5,0,0,0,true)&&p.Trace!.Reason=="corner-roll-declined","50 boundary declines");
        Check(!p.Begin(C(10.1),2,c,0,0,0,0,true),"decline cooldown prevents reroll");
        foreach(var pair in new[]{(0d,"jump"),(.24999,"jump"),(.25,"run-by"),(.39999,"run-by"),(.4,"step"),(.999,"step")})
        {p=Begin(0,pair.Item1);Check(p.Trace!.Style==pair.Item2,"60/25/15 style intervals");}
        p=Begin(0,0,false);Check(p.Trace!.Style=="step","other personalities only lateral");
        p=Planned();double deadline=p.Deadline;p.Shot(10.06,true);Check(p.Trace!.Shots==0,"unobserved angle cannot count firing window");
        p.Observe(F(10.1),c,"");p.Shot(10.1,true);Check(p.Trace.Shots==1&&p.Trace.ExposedShots==1,"native shot observed in open window");
        p=Pending();Check(!p.Trace!.Requested&&!p.Trace.Arrived,"phase change is not a movement request");
        p.Request(10.45);Check(!p.Trace.Arrived&&!p.Trace.HiddenObserved,"accepted request not hidden arrival");
        for(int i=1;i<=9;i++)p.Observe(F(10.45+i*.1),c,"");Check(!p.Active&&p.Trace.Reason=="corner-movement-stalled","unmoved route times out");
        p=Hidden();Check(p.MayKeep(10.66,c),"brief intentional cover preserves native action");
        for(int i=1;i<=10;i++)p.Observe(F(10.65+i*.05,-.75,false),c,"");
        Check(p.Trace!.State=="peek-pending","hold duration starts after arrival");
        Check(p.Request(11.15),"outward requested");
        for(int i=1;i<=4;i++){var f=F(11.15+i*.05,-.75+.75*i/4,i==4);f.AtGoal=i==4;p.Observe(f,c,"");}
        Check(p.Active&&p.Trace.Peeks==1&&p.Trace.OpenObserved,"reacquired personal view confirms outward peek");
        Check(p.Deadline==14,"phases never extend deadline");
        p.Shot(11.35,true);Check(p.Trace.Shots==1&&p.Trace.ExposedShots==1,"shot after outward arrival");
        var fixture=p.Trace;
        // Script a full physical sequence with two observed arrivals and covered returns.
        p=Planned();double time=10.05;double x=0;double motionStart=0,motionFrom=0;string phase="";
        for(int tick=0;tick<100&&p.Active;tick++) {
            time=Math.Round(time+.05,6);
            if(p.WantsRoute){p.Request(time);motionStart=time;motionFrom=x;phase=p.Trace!.State;}
            bool at=false;
            if(p.Moving){double u=Math.Min(1,(time-motionStart)/.2);x=motionFrom+(p.Goal.X-motionFrom)*u;at=u>=1;}
            bool visible=x>-.1;var f=F(time,x,visible);f.AtGoal=at;p.Observe(f,c,"");
        }
        Check(!p.Active&&p.Trace!.Peeks==2&&p.Trace.Reason=="corner-peeks-completed","two peeks finish after covered return: "+p.Trace!.State+" "+p.Trace.Reason+" "+p.Trace.Peeks);
        Check(p.NextAttempt>=time+3.99,"cooldown measured from actual finish");
        p=Planned(.3);for(int i=1;i<=8;i++)p.Observe(F(10.05+i*.05),c,"");p.Request(10.45);
        for(int i=1;i<=4;i++){var f=F(10.45+i*.05,-.75*i/4,false);f.AtGoal=i==4;p.Observe(f,c,"");}
        Check(!p.Active&&p.Trace!.Reason=="corner-crossing-completed"&&p.Trace.Peeks==0,"one crossing ends without repeated traversals");
        p=Hidden();for(int i=1;i<=15&&p.Active;i++)p.Observe(F(10.65+i*.1,-.75,false),c,"");
        Check(!p.Active&&p.Trace!.Reason=="corner-personal-contact-expired","unseen target cannot extend grace");
        p=Hidden();var unseen=F(10.7,-.75,false);unseen.Combat.Known=new(999,0,999);p.Observe(unseen,c,"");
        Check(p.Known.X==0&&p.Known.Z==12,"hidden position changes ignored");
        p=Hidden();Check(!p.MayKeep(10.86,c),"stale telemetry cannot retain decision");
        p=Pending();p.Request(10.45);var noCover=F(10.5,-.25,false);noCover.AtGoal=true;noCover.Hidden=false;
        Check(!p.Observe(noCover,c,"")&&p.Trace!.Reason=="corner-arrival-not-protected","arrival without occlusion is not cover");
        foreach(var mutate in new Action<CornerPeekFrame>[] {
            f=>f.Combat.Ghost=true,f=>f.Combat.Pmc=false,f=>f.Combat.Authority=false,f=>f.Combat.Active=false,f=>f.Combat.Enabled=false,
            f=>f.Combat.Busy=true,f=>f.Combat.Urgent=true,f=>f.Combat.Ready=false,f=>f.Combat.Immobile=true,f=>f.Combat.ProtectedCover=true,
            f=>f.Combat.Grounded=false,f=>f.Combat.Sprinting=true,f=>f.Combat.NativeRouteValid=false,f=>f.Combat.Target="other",
            f=>f.Combat.Action="Search",f=>f.RouteValid=false,f=>f.GeometryFresh=false,f=>f.LaneSafe=false,f=>f.SinceHit=.99,
            f=>f.Combat.Distance=4.99,f=>f.Combat.Distance=25.01,f=>f.Combat.Known=new(double.NaN,0,0),f=>f.Speed=double.NaN
        }) {p=Planned();var f=F(10.1);mutate(f);Check(!p.Observe(f,c,"")&&!p.Active,"safety interruption in exposed phase");
            p=Hidden();f=F(10.7,-.75,false);mutate(f);Check(!p.Observe(f,c,"")&&!p.Active,"safety interruption in hidden phase");}
        p=Planned();Check(!p.Observe(F(11),c,"")&&p.Trace!.Reason=="corner-invalid-or-stale-observation","callback gap ends");
        p=Planned();Check(!p.Observe(F(9),c,""),"backward clock ends");
        p=Planned();var drift=F(10.1,.21);Check(!p.Observe(drift,c,"")&&p.Trace!.Reason=="corner-hold-position-lost","exposed hold cannot drift away");
        p=Hidden();drift=F(10.7,-.75,false);drift.Speed=.36;Check(!p.Observe(drift,c,"")&&p.Trace!.Reason=="corner-hold-position-lost","hidden pause needs a real stable stop");
        p=Begin();for(int i=1;i<=5;i++)p.Observe(F(10+i*.1),c,"");Check(!p.Active&&p.Trace!.Reason=="corner-planning-timeout","bounded planning");
        p=Planned();var expiry=F(14);Check(!p.Observe(expiry,c,"native emergency"),"emergency always wins");
        p=Hidden();p.Fallback("unsafe jump");Check(p.Trace!.Style=="step"&&p.Trace.Alternatives.Length==1,"record fallback");
        // A jump is requested only with lateral momentum, then flight/landing require separate observations.
        p=Hidden();p.Trace!.Style="jump";for(int i=1;i<=10;i++)p.Observe(F(10.65+i*.05,-.75,false),c,"");p.Request(11.15);
        Check(!p.MayJump(11.15,.9,.6,true,true,80)&&!p.MayJump(11.15,1.2,.6,true,true,29),"no in-place or exhausted jump");
        Check(p.MayJump(11.15,1.2,.6,true,true,80),"moving jump eligible");p.JumpAccepted(true);p.JumpAccepted(true);
        Check(p.Trace.Jumps==1&&!p.Trace.AirborneObserved&&!p.MayJump(11.15,1.2,.6,true,true,80),"one request is not flight");
        var air=F(11.2,-.5,false);air.Grounded=air.Combat.Grounded=false;p.Observe(air,c,"");Check(p.Trace.AirborneObserved,"airborne observed");
        var land=F(11.3,0,true);land.AtGoal=true;p.Observe(land,c,"");Check(p.Trace.LandedObserved,"landing observed");
        Check(CornerPeek.Lane(new(0,0,0),new(1,0,0),new(.5,.7,.1),true),"jump vertical envelope");
        Check(!CornerPeek.Lane(new(0,0,0),new(1,0,0),new(.5,.7,.1)),"walking cannot float");
        Check(!CornerPeek.Lane(new(0,0,0),new(1,0,0),new(.5,0,.4)),"lateral corridor escape");
        foreach(var prop in typeof(CornerPeekConfig).GetProperties().Where(x=>x.PropertyType==typeof(double)))
            foreach(var v in new[]{double.NaN,double.PositiveInfinity,-1d,1001d}){var bad=new CornerPeekConfig();prop.SetValue(bad,v);Invalid(bad.Validate);}
        Invalid(()=>new CornerPeekConfig{MaxPeeks=3}.Validate());Invalid(()=>new CornerPeekConfig{JumpShare=90,RunShare=11}.Validate());
        Invalid(()=>new CornerPeekConfig{HiddenMin=.4,HiddenMax=.2}.Validate());Invalid(()=>new CornerPeekConfig{ExposedMin=.8,ExposedMax=.4}.Validate());
        Invalid(()=>new CornerPeekConfig{MinDistance=20,MaxDistance=10}.Validate());Invalid(()=>new CometConfig{CornerPeek=null!}.Validate());
        var legacy=JsonSerializer.Deserialize<CometConfig>("{}")!;legacy.Validate();Check(legacy.CornerPeek.Chance==50,"legacy config default");
        var folder=Path.Combine(Path.GetTempPath(),"comet-m03-"+Guid.NewGuid().ToString("N"));var file=Path.Combine(folder,"config.json");
        try {var store=new ConfigStore(file);var config=store.Read();config.CornerPeek.Chance=60;store.Save(config,config.Revision);
            Check(store.Read().CornerPeek.Chance==60,"server persistence");var original=File.ReadAllText(file);config=store.Read();config.CornerPeek.Chance=101;
            Invalid(()=>store.Save(config,config.Revision));Check(original==File.ReadAllText(file),"invalid edit atomic");}
        finally {foreach(var path in new[]{file,file+".bak"})if(File.Exists(path))File.Delete(path);if(Directory.Exists(folder))Directory.Delete(folder);}
        File.WriteAllText("artifacts/m03-corner-peek.json",JsonSerializer.Serialize(new{cornerPeek=fixture},new JsonSerializerOptions{IncludeFields=true,PropertyNamingPolicy=JsonNamingPolicy.CamelCase,WriteIndented=true}));
        Console.WriteLine($"M03: {checks} checks passed (cover phases, actual movement, grace, interruption, styles, config)");return checks;
    }
}
