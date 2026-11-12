using Comet.Core;
using System;
using System.Linq;
using System.Collections.Generic;
// Reference: R_F SAIN zzap fbb86b838e0fa692b759b2c0b11fc30158a53876.
internal static class FearTests
{
    internal static int Run()
    {
        int n=0;void Check(bool ok,string why){n++;if(!ok)throw new Exception("Fear: "+why);}
        void Near(double actual,double expected,string why)=>Check(Math.Abs(actual-expected)<1e-9,why);

        var c=new CometConfig().Fear;var model=new FearModel(c);
        var f=new FearInput{Now=1,Personality="Normal",Health=1};Near(model.Update(f).Level,.1,"normal base");
        f.Now=2;f.Health=.35;f.KnownEnemies=2;f.AvailableAllies=1;f.HeavySuppression=true;f.NearbyDeathAt=1;f.PlayerStreak=.25;
        Near(model.Update(f).Level,1,"source clamps combined fear");Check(model.Current.Panic,"panic above .75");
        f.Personality="Wreckless";Near(model.Update(f).Level,(.25*.65+.15+.1+.3+.25)*.3,"personality sensitivity");
        f=new(){Now=3,Personality="Normal"};Near(model.Update(f).Level,.1,"no added recovery delay or smoothing");Check(!model.Current.Panic,"no panic exit hysteresis");
        c.Enabled=false;Near(model.Update(f).Level,0,"feature toggle");c.Enabled=true;
        var memory=new FearRaidMemory();memory.Death(7,new(0,0,0),10);
        Check(memory.NearbyDeath(7,new(39.99,0,0),20)==10,"same group within 40 metres");
        Check(memory.NearbyDeath(8,new(0,0,0),20)==null&&memory.NearbyDeath(7,new(40,0,0),20)==null,"other group and boundary excluded");
        Check(memory.NearbyDeath(7,new(0,0,0),40)==null,"death memory expires after 30 seconds");
        memory.PlayerKill(20);Near(memory.Streak(true,30),.05,"current-raid human streak");Near(memory.Streak(false,30),0,"no streak against AI");Near(memory.Streak(true,320),0,"five-minute expiry");
        for(int i=0;i<10;i++)memory.PlayerKill(30);Near(memory.Streak(true,31),.25,"streak bonus cap");
        memory.Clear();Check(memory.NearbyDeath(7,new(),20)==null&&memory.Streak(true,31)==0,"raid teardown clears all memory");
        for(int i=0;i<65;i++)memory.Death(i,new(),10);Check(memory.NearbyDeath(0,new(),11)==null&&memory.NearbyDeath(1,new(),11)==10,"source 64-death bounded history");
        var s=new Situation{Health=1,Ammo=1,PathLength=10,KnownDistance=10};var calm=ZzapUtility.Hidden(s,.5,0,false);s.Fear=new(){Level=.5};var afraid=ZzapUtility.Hidden(s,.5,0,false);
        Near(afraid[Tactic.Push]-calm[Tactic.Push],-20,"source push fear penalty");Near(afraid[Tactic.FallBack]-calm[Tactic.FallBack],25,"source retreat fear bonus");

        return n;
    }
}
