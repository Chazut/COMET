using Comet.Core;
using Comet.Server;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

static class ImpactReactionTests
{
    public static int Run()
    {
        int n=0;
        void Check(bool ok,string why){if(!ok)throw new Exception("P07: "+why);n++;}
        void Near(double a,double b,string why)=>Check(Math.Abs(a-b)<1e-8,why);
        var config=new CometConfig();config.Validate();var c=config.ImpactReaction;
        Check(c.Enabled&&c.Memory==3&&c.MinimumInterval==.5,"defaults");
        var old=JsonSerializer.Deserialize<CometConfig>("{\"Revision\":8,\"Enabled\":false}")!;old.Validate();
        Check(!old.Enabled&&old.ImpactReaction.Memory==3&&old.Revision==8,"old config independent defaults");
        foreach(var edit in new Action<ImpactReactionConfig>[] {x=>x.Memory=double.NaN,x=>x.Memory=.9,x=>x.Memory=9,x=>x.MinimumInterval=.09,x=>x.MinimumInterval=2.1,x=>x.MinimumInterval=double.PositiveInfinity,x=>{x.Memory=1;x.MinimumInterval=2;}})
        {var bad=new ImpactReactionConfig();edit(bad);try{bad.Validate();throw new Exception("Invalid config accepted");}catch(ArgumentException){n++;}}
        try{new CometConfig{ImpactReaction=null!}.Validate();throw new Exception("Null section accepted");}catch(ArgumentException){n++;}
        var dir=Path.Combine(Path.GetTempPath(),"comet-p07-"+Guid.NewGuid().ToString("N"));
        try{
            var store=new ConfigStore(Path.Combine(dir,"config.json"));var saved=store.Read();saved.ImpactReaction.Memory=4;saved.ImpactReaction.MinimumInterval=.75;saved.ImpactReaction.Enabled=false;
            store.Save(saved,saved.Revision);var loaded=store.Read();Check(!loaded.ImpactReaction.Enabled&&loaded.ImpactReaction.Memory==4,"actual server save/load");
            var game=JsonSerializer.Deserialize<CometConfig>(store.ForGame())!;Check(game.ImpactReaction.MinimumInterval==.75&&!game.ImpactReaction.Enabled,"authoritative client response");
        }finally{if(Directory.Exists(dir))Directory.Delete(dir,true);}
        ImpactFrame F(double at=10)=>new(){Now=at,DirectionZ=1};
        var m=new ImpactReaction(c);var t=m.Receive(F());Check(t.Active&&m.Due(10)&&t.Hits==1,"first impact immediate");
        Check(t.Contact==""&&t.Evidence=="impact-sector"&&t.SectorYaw==180,"anonymous reverse trajectory");
        Check(!t.NativeAccepted&&!t.ActionObserved&&!t.MovementObserved,"impact is not reaction success");
        m.Reviewed(10);Check(!m.Due(10.5),"no repeat without impact");
        var second=F(10.1);second.DirectionZ=0;second.DirectionX=1;m.Receive(second);
        Check(m.Current.SectorYaw==180&&!m.Due(10.49)&&m.Due(10.5),"burst direction held until review");
        Check(m.Reviewed(10.5)&&m.Current.SectorYaw==270,"pending latest direction preserved without another hit");
        Check(!m.Reviewed(10.6)&&!m.Pending,"cannot bypass interval");
        Near(m.Snapshot(12).Remaining,1.1,"latest hit extends memory");Check(m.Active(13.099)&&!m.Active(13.1),"exact memory expiry");
        m.End(13.1,"expired");Check(!m.Current.Active&&!m.Pending&&m.Current.Contact==""&&m.Current.SectorYaw==null,"cleanup clears evidence");
        Check(m.Receive(F(1)).Hits==1&&m.Due(1),"new raid may use a new clock");
        foreach(var edit in new Action<ImpactFrame>[] {f=>f.Scoped=false,f=>f.Bullet=false,f=>f.Delayed=true,f=>f.Friendly=true,f=>f.Now=double.NaN,f=>f.Now=double.PositiveInfinity})
        {var r=new ImpactReaction(c);var f=F();edit(f);var rejected=r.Receive(f);Check(rejected.State=="rejected"&&!r.Current.Active&&!r.Due(10),"rejected damage cannot start a reaction");}
        Check(new ImpactReaction(new(){Enabled=false}).Receive(F()).State=="rejected","disabled");
        var clock=new ImpactReaction(c);clock.Receive(F());Check(clock.Receive(F(9)).State=="rejected"&&!clock.Active(9),"reversed time");
        foreach(var evidence in new[]{"seen","heard-shot","injury","shared","movement","", "nearby"})
        {var f=F();f.Contact="hidden-person";f.Evidence=evidence;var r=new ImpactReaction(c);t=r.Receive(f);Check((t.Contact.Length>0)==ImpactReaction.PersonalEvidence(evidence),"only independent seen/heard-shot permits ID "+evidence);}
        foreach(var xyz in new[]{(0d,0d,0d),(0d,1d,0d),(.1,1d,0d),(double.NaN,0d,1d),(0d,0d,double.PositiveInfinity)})
            Check(ImpactReaction.Sector(xyz.Item1,xyz.Item2,xyz.Item3)==null,"invalid/vertical direction unknown");
        for(int angle=0;angle<360;angle++)
        {
            var yaw=ImpactReaction.Sector(-Math.Sin(angle*Math.PI/180),0,-Math.Cos(angle*Math.PI/180));
            Check(yaw.HasValue&&yaw.Value%90==0,"sector quantized");
            double delta=Math.Abs(((yaw!.Value-angle+540)%360)-180);Check(delta<=45.00001,"sector covers incoming direction");
        }
        Check(ImpactReaction.Sector(0,0,-1)==0&&ImpactReaction.Sector(-1,0,0)==90,"cardinal conventions");
        Check(ImpactPolicy.Priority(true,false,false,false,false,false)=="","idle PMC allowed");
        Check(ImpactPolicy.Priority(true,false,false,false,true,true)=="","owned native Freeze can continue");
        foreach(bool owned in new[]{false,true})
        {
            Check(ImpactPolicy.Priority(false,false,false,false,owned,false)=="impact-out-of-scope","scope revokes even owned reaction");
            Check(ImpactPolicy.Priority(true,true,false,false,owned,false)=="impact-error-cooldown","error fallback");
            Check(ImpactPolicy.Priority(true,false,true,false,owned,false)=="impact-native-contact","real contact wins");
            Check(ImpactPolicy.Priority(true,false,false,true,owned,false)=="impact-native-priority","healing/reload/grenade/squad wins");
        }
        Check(ImpactPolicy.Priority(true,false,false,false,false,true)=="impact-native-response","existing combat not stolen");
        Check(ImpactPolicy.Moved(3,2.7,.3),"movement toward protection observed");
        Check(!ImpactPolicy.Moved(3,3.1,.3)&&!ImpactPolicy.Moved(3,3,.4)&&!ImpactPolicy.Moved(3,2.9,.4),"sideways/backwards/jitter not claimed as progress");
        Check(!ImpactPolicy.Moved(3,double.NaN,.4),"invalid progress rejected");
        Check(ImpactPolicy.Protection(0,2,true,3,4,.2,0),"better nearby cover allowed");
        foreach(var v in new[]{(2,2,3,4d,.2,0d),(0,1,3,4d,.2,0d),(0,2,9,4d,.2,0d),(0,2,3,5.01,.2,0d),(0,2,3,4d,.76,0d),(0,2,3,4d,.2,.51),(0,2,1,0d,.2,0d),(0,2,3,double.NaN,.2,0d)})
            Check(!ImpactPolicy.Protection(v.Item1,v.Item2,true,v.Item3,v.Item4,v.Item5,v.Item6),"unsafe/long/non-improving route rejected");
        Check(ImpactPolicy.Protection(0,1,false,2,2,0,2),"unlocated local geometry does not invent a direction");
        using(var doc=JsonDocument.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(new{feature="P07",stage="evaluated",impact=m.Snapshot(1)},Comet.Client.Diagnostics.Json)))
            Check(!doc.RootElement.GetProperty("impact").GetProperty("movementObserved").GetBoolean(),"wire observation is explicit");
        using(var pe=new PEReader(File.OpenRead("references/sain-4.5.1/SAIN.dll")))
        {
            var md=pe.GetMetadataReader();
            MethodDefinition Method(string type,string method)=>md.TypeDefinitions.Select(md.GetTypeDefinition).Single(t=>md.GetString(t.Name)==type).GetMethods().Select(md.GetMethodDefinition).Single(m=>md.GetString(m.Name)==method);
            List<string> Calls(string type,string method)=>FloorHearingTests.ReadIl(pe.GetMethodBody(Method(type,method).RelativeVirtualAddress).GetILBytes()!).Where(x=>x.op==OpCodes.Call||x.op==OpCodes.Callvirt).Select(x=>FloorHearingTests.Name(md,(int)x.operand!)).ToList();
            Check(Method("SAINBotMedicalClass","GetHit").GetParameters().Select(md.GetParameter).Count(p=>p.SequenceNumber>0)==3,"real impact hook signature");
            Check(Calls("SAINBotMedicalClass","GetHit").Count(x=>x=="GetHit")>=2,"native medical responses remain before postfix");
            var dc=Calls("BotDecisionManager","getDecision");Check(dc.Contains("SetDecisions")&&dc.Contains("ChooseEnemy")&&dc.IndexOf("ShallAvoidGrenade")<dc.IndexOf("SetDecisions"),"native no-contact decision path exists");
            var freeze=Calls("FreezeAction","Update");Check(freeze.Contains("SetPoseToCover"),"native anonymous defensive posture contract");
            var layerIl=FloorHearingTests.ReadIl(pe.GetMethodBody(Method("CombatSoloLayer","IsActive").RelativeVirtualAddress).GetILBytes()!);
            Check(layerIl.Any(x=>(x.op==OpCodes.Ldfld||x.op==OpCodes.Call||x.op==OpCodes.Callvirt)&&new[]{"_currentDecision","get__currentDecision"}.Contains(FloorHearingTests.Name(md,(int)x.operand!))),"native layer driven by accepted decision");
            Check(Calls("PoseClass","ManualUpdate").Contains("get_HasEnemy"),"native pose skips anonymous threat; explicit guarded fallback required");
            Check(Method("BotAction","OnSteeringTicked").RelativeVirtualAddress!=0,"steering callback contract");
        }
        using(var pe=new PEReader(File.OpenRead("Client/bin/Release/netstandard2.1/COMET.dll")))
        {
            var md=pe.GetMetadataReader();var plugin=md.TypeDefinitions.Select(md.GetTypeDefinition).Single(t=>md.GetString(t.Name)=="Plugin");
            var hit=plugin.GetMethods().Select(md.GetMethodDefinition).Single(x=>md.GetString(x.Name)=="ImpactHit");
            var calls=FloorHearingTests.ReadIl(pe.GetMethodBody(hit.RelativeVirtualAddress).GetILBytes()!).Where(x=>x.op==OpCodes.Call||x.op==OpCodes.Callvirt||x.op==OpCodes.Ldfld).Select(x=>FloorHearingTests.Name(md,(int)x.operand!)).ToList();
            foreach(string forbidden in new[]{"get_Position","MasterOrigin","CheckAddEnemy","AddEnemy","SetUnderFire","UpdatePersonalHeardPosition"})Check(!calls.Contains(forbidden),"hit adapter cannot inject hidden evidence: "+forbidden);
        }
        Console.WriteLine($"P07: {n} checks passed (impact memory, priorities, coarse direction, protection policy, config persistence and native IL).");return n;
    }
}
