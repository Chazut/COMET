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
  var f=Frame();f.Options.Add(Surg());C(new MedicalChoice().Evaluate(f,c).Choice=="Surgery","safe surgery");
  foreach(var mutation in new Action<MedicalFrame>[] {x=>x.Moving=true,x=>x.ProtectiveCover=false,x=>x.ThreatsIncomplete=true,x=>x.HeavyBleed=true,x=>x.LightBleed=true,x=>x.Vital=true,x=>x.Quiet=24.99})
  {f=Frame();f.Options.Add(Surg());mutation(f);C(new MedicalChoice().Evaluate(f,c).Choice=="Surgery","no added hard veto for a source-eligible surgery");}
  foreach(var mutation in new Action<MedicalFrame>[] {x=>x.Visible=true,x=>x.Footsteps=true,x=>x.Priority="grenade"})
  {f=Frame();f.Options.Add(Surg());mutation(f);C(new MedicalChoice().Evaluate(f,c).Choice=="Wait","source pressure/wait or native urgency wins");}
  f=Frame();f.Options.Add(Aid());f.SinceHit=1;var aid=new MedicalChoice().Evaluate(f,c);C(aid.Alternatives.Single(x=>x.Action=="FirstAid").Score>0,"recent hit penalizes without banning treatment");
  f=Frame();f.Options.Add(Stim());C(new MedicalChoice().Evaluate(f,c).Choice=="Wait","healthy bot preserves stimulant");f.Health=.3;C(new MedicalChoice().Evaluate(f,c).Choice=="Stims","low-health stimulant");
  f=Frame();f.Options.Add(Surg());var p=new MedicalChoice();var selected=p.Evaluate(f,c);C(!selected.Started&&!selected.Applied,"selection is not an observed medical operation");p.Request(11);p.ObserveStart(12);C(p.Trace!.Started&&p.Trace.Applied,"observed native start");
  f.Options.Clear();C(ReferenceEquals(p.Evaluate(f,c),selected),"ongoing native treatment retained");p.End(30,"ended",c);C(!p.Active&&p.Trace.Outcome=="unconfirmed","no invented completion");
  f.Now=30.01;f.Options.Add(Aid());C(p.Evaluate(f,c).Choice=="FirstAid","no extra COMET treatment cooldown");
  using var pe=new PEReader(File.OpenRead(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../references/sain-4.5.1/SAIN.dll"))));var md=pe.GetMetadataReader();
  foreach(var (type,method,count) in new[]{("SelfActionDecisionClass","StartBotHeal",1),("SelfActionDecisionClass","CheckContinueSelfAction",2),("SAINSelfActionClass","DoFirstAid",0),("SAINSelfActionClass","DoStims",0),("SAINSelfActionClass","DoSurgery",0),("DoSurgeryAction","checkDoSurgery",0),("DoSurgeryAction","Stop",0)}){
   var td=md.TypeDefinitions.Select(md.GetTypeDefinition).Single(t=>md.GetString(t.Name)==type);
   C(td.GetMethods().Select(md.GetMethodDefinition).Any(m=>md.GetString(m.Name)==method&&m.GetParameters().Count==count),"pinned native contract "+type+"."+method);
  }
  Console.WriteLine($"H01: {n} checks passed");return n;
 }
}
