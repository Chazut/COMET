using System;
using Comet.Core;
using System.Text.Json;
internal static class MedicalPostureTests
{
 public static int Run()
 {
  int n=0;void C(bool ok,string why){n++;if(!ok)throw new Exception("H03: "+why);}
  var c=new MedicalPostureConfig();C(c.Enabled&&c.Interval==.1&&c.ResumeDelay==.2,"defaults");
  foreach(double invalid in new[]{double.NaN,double.PositiveInfinity,-1d,2d})foreach(bool interval in new[]{false,true}){
   var v=new MedicalPostureConfig();if(interval)v.Interval=invalid;else v.ResumeDelay=invalid;
   bool rejected=false;try{v.Validate();}catch(ArgumentException){rejected=true;}C(rejected,"invalid bounds rejected");}
  C(JsonSerializer.Deserialize<CometConfig>("{}")!.MedicalPosture.Enabled,"old configs get H03 defaults");
  var config=new CometConfig();config.MedicalPosture.ResumeDelay=.35;config.Validate();
  C(JsonSerializer.Deserialize<CometConfig>(JsonSerializer.Serialize(config))!.MedicalPosture.ResumeDelay==.35,"config roundtrip");
  foreach(string kind in new[]{"FirstAid","Stims","Surgery","Native medicine"}){
   var p=new MedicalPosture();var f=new MedicalPostureFrame{Now=10,Using=true,Treatment=kind,Tilt=5,Grounded=true};
   C(p.Tick(f,c)&&p.Trace!.Treatment==kind,"all treatments protect");
   f.Now=10.1;f.Using=false;f.MedicineHands=true;C(p.Tick(f,c)&&p.Trace!.State=="protecting","cancel flag before hands end");
   f.Now=10.2;f.MedicineHands=false;C(p.Tick(f,c)&&p.Trace!.State=="waiting-weapon","hands empty not ready");
   f.Now=10.3;f.WeaponReady=true;C(p.Tick(f,c)&&p.Trace!.State=="centering","ready weapon but bust out");
   f.Now=10.4;f.Tilt=0;f.NeutralTarget=true;C(p.Tick(f,c)&&p.Trace!.State=="settling","stable timer starts on evidence");
   f.Now=10.5;C(p.Tick(f,c),"not early");f.Now=10.6;C(!p.Tick(f,c)&&p.Trace!.State=="resumed","resume exactly after stable window");
   f.Now=11;f.Using=true;C(p.Tick(f,c),"next operation new guard");p.End("teardown");C(!p.Blocking,"clear on teardown");
  }
  foreach(string change in new[]{"medicine","weapon","tilt","target","gap","clock","nan"}){
   var p=new MedicalPosture();var f=new MedicalPostureFrame{Using=true,Now=1};p.Tick(f,c);
   f.Using=false;f.Now=1.1;f.WeaponReady=f.NeutralTarget=true;p.Tick(f,c);f.Now=1.2;
   switch(change){case"medicine":f.MedicineHands=true;break;case"weapon":f.WeaponReady=false;break;case"tilt":f.Tilt=.11;break;case"target":f.NeutralTarget=false;break;case"gap":f.Now=2;break;case"clock":f.Now=1;break;default:f.Tilt=double.NaN;break;}
   C(p.Tick(f,c),change+" cannot bypass stability");f.Now+=.1;f.MedicineHands=false;f.WeaponReady=f.NeutralTarget=true;f.Tilt=0;
   C(p.Tick(f,c),change+" waits again");
  }
  {var p=new MedicalPosture();C(!p.Tick(new(){Now=1,WeaponReady=false,Tilt=5},c),"no medicine no new guard");p.Tick(new(){Now=2,Using=true},c);c.Enabled=false;C(!p.Tick(new(){Now=2.1,Using=true},c),"disabled releases");c.Enabled=true;c.ResumeDelay=0;C(p.Tick(new(){Now=3,MedicineHands=true},c),"zero delay still protects hands");C(!p.Tick(new(){Now=3.1,WeaponReady=true,NeutralTarget=true},c),"zero delay configurable");}
  Console.WriteLine($"{n} H03 policy checks passed");return n;
 }
}
