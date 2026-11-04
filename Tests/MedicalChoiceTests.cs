using Comet.Core;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
internal static class MedicalChoiceTests
{
 public static int Run()
 {
  int n=0;void C(bool ok,string why){n++;if(!ok)throw new Exception("H01: "+why);}
  var c=new MedicalChoiceConfig();c.Validate();C(c.Enabled&&c.Interval==.5&&c.HitDelay==2&&c.EmergencyStimDelay==.5&&c.StimHealth==.35&&c.FootstepAge==5&&c.FootstepDistance==30&&c.SurgeryQuiet==25&&c.SurgeryDistance==40&&c.RetryDelay==3,"defaults");
  foreach(var prop in typeof(MedicalChoiceConfig).GetProperties().Where(p=>p.PropertyType==typeof(double)))
   foreach(var value in new[]{double.NaN,double.PositiveInfinity,double.NegativeInfinity,-1d,100001d}){var invalid=new MedicalChoiceConfig();prop.SetValue(invalid,value);try{invalid.Validate();C(false,"invalid "+prop.Name);}catch(ArgumentException){n++;}}
  var config=Newtonsoft.Json.JsonConvert.DeserializeObject<CometConfig>("{}")!;config.Validate();C(config.MedicalChoice.Enabled,"old configs get defaults");
  config.MedicalChoice=new(){Enabled=false,Interval=1,HitDelay=3,EmergencyStimDelay=1,StimHealth=.5,FootstepAge=8,FootstepDistance=20,SurgeryQuiet=30,SurgeryDistance=50,RetryDelay=5};
  string json=Newtonsoft.Json.JsonConvert.SerializeObject(config);var copy=Newtonsoft.Json.JsonConvert.DeserializeObject<CometConfig>(json)!;copy.Validate();C(Newtonsoft.Json.JsonConvert.SerializeObject(copy)==json,"server settings persist all values");
  config.MedicalChoice=null!;try{config.Validate();C(false,"null accepted");}catch(ArgumentException){n++;}
  MedicalFrame Frame()=>new(){Now=10,Contacts=1,Contact="known",X=50,Y=0,Z=0,Nearest=50,Quiet=30,ReachedCover=true,ProtectiveCover=true,Health=.6};
  MedicalOption Aid(string need="injury")=>new(){Action="FirstAid",Usable=true,Item="aid",Part="Chest",Need=need,Duration=4};
  MedicalOption Stim()=>new(){Action="Stims",Usable=true,Item="stim",Part="Chest",Need="low-health",Duration=2};
  MedicalOption Surg()=>new(){Action="Surgery",Usable=true,Item="kit",Part="LeftLeg",Need="destroyed-limb",Duration=20};
  var f=Frame();f.Options.Add(Surg());C(new MedicalChoice().Evaluate(f,c).Choice=="Surgery","quiet protected surgery");
  foreach(var block in new[]{"moving","unreached","exposed","visible","steps","incomplete","new","near","heavy","light","vital","hit","grenade"}){
   f=Frame();f.Options.Add(Surg());switch(block){case"moving":f.Moving=true;break;case"unreached":f.ReachedCover=false;break;case"exposed":f.ProtectiveCover=false;break;case"visible":f.Visible=true;break;case"steps":f.Footsteps=true;break;case"incomplete":f.ThreatsIncomplete=true;break;case"new":f.Quiet=24.99;break;case"near":f.Nearest=40;break;case"heavy":f.HeavyBleed=true;break;case"light":f.LightBleed=true;break;case"vital":f.Vital=true;break;case"hit":f.SinceHit=1.99;break;default:f.Priority="grenade";break;}C(new MedicalChoice().Evaluate(f,c).Choice=="Wait",block+" refuses surgery");}
  f=Frame();f.Options.Add(Aid("heavy-bleed"));f.Options.Add(Stim());f.Options.Add(Surg());f.HeavyBleed=true;f.Health=.2;f.Visible=true;f.ProtectiveCover=false;
  C(new MedicalChoice().Evaluate(f,c).Choice=="FirstAid","real bleeding first even exposed");
  f.Options[0].Usable=false;C(new MedicalChoice().Evaluate(f,c).Choice=="Stims","useful emergency stim if aid impossible");
  f.SinceHit=.49;C(new MedicalChoice().Evaluate(f,c).Choice=="Wait","not in hit frame");f.SinceHit=.5;C(new MedicalChoice().Evaluate(f,c).Choice=="Stims","emergency delay boundary");
  f=Frame();f.Options.Add(Stim());C(new MedicalChoice().Evaluate(f,c).Choice=="Wait","no wasteful stim");f.Health=.35;C(new MedicalChoice().Evaluate(f,c).Choice=="Stims","35 percent boundary");
  f=Frame();f.Options.Add(Aid());f.SinceHit=1.99;C(new MedicalChoice().Evaluate(f,c).Choice=="Wait","ordinary hit delay");f.SinceHit=2;C(new MedicalChoice().Evaluate(f,c).Choice=="FirstAid","ordinary delay boundary");
  f=Frame();f.Options.Add(Surg());var p=new MedicalChoice();var selected=p.Evaluate(f,c);C(!selected.Started&&!selected.Applied,"selection not actual use");p.Request(11);C(p.Trace!.State=="requested"&&!p.Trace.Started,"request not start");p.ObserveStart(12);C(p.Trace.Started&&p.Trace.ValidFor==1,"observed hands only");f.Options.Clear();f.Options.Add(Aid());C(ReferenceEquals(p.Evaluate(f,c),selected)&&p.Trace.Choice=="Surgery","no rescore during real operation");p.End(30,"ended",c);C(!p.Active&&!p.Trace.Applied&&p.Trace.Outcome=="unconfirmed","no invented completion");f.Now=32.99;C(p.Evaluate(f,c).Choice=="Wait","retry backoff");f.Now=33;C(p.Evaluate(f,c).Choice=="FirstAid","retry expires");
  f=Frame();f.Options.Add(Surg());f.Options.Add(Aid());f.Aggression=.1;C(new MedicalChoice().Evaluate(f,c).Choice=="Surgery","cautious safe limb repair before minor HP");f.Aggression=.9;C(new MedicalChoice().Evaluate(f,c).Choice=="FirstAid","aggressive prefers quicker ordinary treatment");
  using var pe=new PEReader(File.OpenRead(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../references/sain-4.5.1/SAIN.dll"))));var md=pe.GetMetadataReader();
  foreach(var (type,method,count) in new[]{("SelfActionDecisionClass","StartBotHeal",1),("SelfActionDecisionClass","CheckContinueSelfAction",2),("SAINSelfActionClass","DoFirstAid",0),("SAINSelfActionClass","DoStims",0),("SAINSelfActionClass","DoSurgery",0),("DoSurgeryAction","checkDoSurgery",0),("DoSurgeryAction","Stop",0)}){
   var td=md.TypeDefinitions.Select(md.GetTypeDefinition).Single(t=>md.GetString(t.Name)==type);
   C(td.GetMethods().Select(md.GetMethodDefinition).Any(m=>md.GetString(m.Name)==method&&m.GetParameters().Count==count),"pinned native contract "+type+"."+method);
  }
  Console.WriteLine($"H01: {n} checks passed");return n;
 }
}
