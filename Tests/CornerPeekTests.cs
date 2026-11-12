using System;
using System.Linq;
using System.Collections.Generic;
using Comet.Core;

// zzap fbb86b83 behavior; see SOURCE_BASELINE.md.
internal static class CornerPeekTests
{
 public static int Run()
 {
  int n=0;void Check(bool ok,string why){n++;if(!ok)throw new Exception("CornerPeek: "+why);}

 var c=new CornerPeekConfig();c.Validate();Check(c.ExposedMin==.25&&c.ExposedMax==.45&&c.HiddenMin==.18&&c.HiddenMax==.32,"source tap timing");
 var f=new CombatStepFrame{Now=10,Target="e",Known=new(0,0,12),Distance=12};var p=new CornerPeek();
 Check(p.Begin(f,1,c,0,.99,0,0,true),"source peek start");Check(p.Plan(new(1,0,0),"step",10),"opening prepared");
 var view=new CornerPeekFrame{Combat=f,RouteValid=true};f.Now=10.61;Check(p.Observe(view,c,"")&&p.WantsRoute,"initial source firing delay");
 Check(p.Request(10.61),"hide tap");f.Now=11;Check(p.Observe(view,c,"")&&p.Holding,"tap ends without arrival proof");
 f.Now=11.4;Check(p.Observe(view,c,"")&&p.WantsRoute,"next tap available");
 Check(p.Deadline>10000,"no added four second sequence limit");

 return n;
 }
}
