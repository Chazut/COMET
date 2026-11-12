using System;
using System.Linq;
using Comet.Core;

// Behavioral contract: zzap fbb86b83 (see SOURCE_BASELINE.md).
internal static class LightDisciplineTests
{
 public static int Run()
 {
  int n=0; void Check(bool ok,string why){n++;if(!ok)throw new Exception("LightDiscipline: "+why);}

  var c=new LightDisciplineConfig();c.Validate();
  Check(c.ContactLifetime==60&&c.MaxDistance==60&&c.MaxSpeed==1.2,"source limits");
  LightDisciplineFrame F()=>new(){Now=10,Contact="e",Source="heard",Action="Freeze",ContactAge=45,Distance=40,Speed=1};
  var p=new LightDisciplinePolicy();Check(p.Evaluate(F(),c),"hidden slow hold immediately dark, no personal-vision proof");
  p.Observe(2,0,new[]{"lamp","laser"},true);Check(p.Trace!.ObservedOff&&p.Trace.Requested,"report actual command outcome");
  foreach(var change in new Action<LightDisciplineFrame>[] {f=>f.Visible=true,f=>f.ContactAge=60,f=>f.Distance=60,f=>f.Speed=1.2,f=>f.Action="Search"}){
   var f=F();change(f);Check(!p.Evaluate(f,c),"native exclusion immediately releases");Check(p.Evaluate(F(),c),"eligible hold resumes without stability timer");
  }
  p.Yield("other-command");Check(p.Evaluate(F(),c),"no persistent external-owner veto");
  c.Enabled=false;Check(!p.Evaluate(F(),c),"explicit disable");

  return n;
 }
}
