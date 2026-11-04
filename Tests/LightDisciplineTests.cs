using Comet.Core;
using Newtonsoft.Json;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

internal static class LightDisciplineTests
{
    public static int Run()
    {
        int n=0; void C(bool b,string s){n++;if(!b)throw new Exception("L01: "+s);}
        var c=new LightDisciplineConfig();
        C(c.Enabled&&c.ContactLifetime==15&&c.MaxDistance==60&&c.MaxSpeed==.5&&c.HoldDelay==.5&&c.HiddenDelay==1&&c.VisibleDelay==.25,"approved defaults");
        foreach(var json in new[]{"{}","{\"LightDiscipline\":null}"}){var old=JsonConvert.DeserializeObject<CometConfig>(json)!;old.Validate();C(old.LightDiscipline.Enabled,"migration");}
        foreach(var prop in typeof(LightDisciplineConfig).GetProperties().Where(x=>x.PropertyType==typeof(double)))
        foreach(double bad in new[]{double.NaN,double.PositiveInfinity,-1d,10000d}){var invalid=new LightDisciplineConfig();prop.SetValue(invalid,bad);bool thrown=false;try{invalid.Validate();}catch(ArgumentException){thrown=true;}C(thrown,"reject invalid "+prop.Name);}
        LightDisciplineFrame F(double time=10)=>new(){Now=time,Contact="enemy",Source="personal-hearing",Known=new(10,0,0),ContactAge=1,Distance=10,Speed=0,Action="Freeze",Tactic="native-freeze"};
        var p=new LightDisciplinePolicy(); C(!p.Evaluate(F(),c),"no instant extinction");C(!p.Evaluate(F(10.5),c),"wait hidden delay even when hold settled");C(p.Evaluate(F(11),c),"exact hidden boundary");
        p.Observe(2,0,new[]{"laser:2","lamp:1"},true);C(p.Trace!.Requested&&p.Trace.ObservedOff,"requested versus observed");
        var f=F(11.2);f.Visible=true;C(p.Evaluate(f,c),"brief vision keeps current darkness");f.Now=11.44;C(p.Evaluate(f,c),"within visibility confirmation");f.Now=11.45;C(!p.Evaluate(f,c)&&p.Trace!.State=="released","release at confirmed vision");
        C(!p.Evaluate(F(11.5),c),"new hidden interval starts afresh");C(p.Evaluate(F(12.5),c),"reacquire after stability");C(!p.Trace!.Requested,"no claim from previous command");
        p.Yield("external-owner");C(!p.Evaluate(F(13),c)&&p.Trace!.State=="yielded","no contention retry");C(!p.Evaluate(F(14),c),"yield persists throughout same hold");
        f=F(14.1);f.Speed=1;C(!p.Evaluate(f,c),"moving ends old hold");C(!p.Evaluate(F(14.2),c),"new hold waits");C(p.Evaluate(F(14.7),c),"hidden clock survives movement, independent stable wait");
        foreach(var change in new Action<LightDisciplineFrame>[] {x=>x.ContactAge=15.001,x=>x.ContactAge=-1,x=>x.ContactAge=double.NaN,x=>x.Distance=60.001,x=>x.Distance=double.NaN,x=>x.Speed=.501,x=>x.Speed=double.NaN,x=>x.Known=null,x=>x.Known=new(double.NaN,0,0),x=>x.Source="",x=>x.Contact="",x=>x.Tactic="",x=>x.Block="urgent"})
        {p=new();p.Evaluate(F(),c);p.Evaluate(F(11),c);f=F(12);change(f);C(!p.Evaluate(f,c)&&!p.Dark,"ineligible input releases immediately");}
        p=new();f=F();f.Distance=60;f.ContactAge=15;f.Speed=.5;p.Evaluate(f,c);f.Now=11;C(p.Evaluate(f,c),"inclusive boundaries");
        f.Visible=true;f.Block="firing";C(!p.Evaluate(f,c),"urgent action bypasses confirmation wait");
        p=new();f=F();f.Visible=true;p.Evaluate(f,c);f.Now=20;C(!p.Evaluate(f,c),"never begins dark on visible target");
        p=new();p.Evaluate(F(),c);p.Evaluate(F(11),c);f=F(12);f.Contact="other";C(!p.Evaluate(f,c),"new contact stabilizes");
        p=new();p.Evaluate(F(),c);p.Evaluate(F(11),c);C(!p.Evaluate(F(1),c),"clock rollback drops ownership");
        C(!p.Evaluate(F(double.NaN),c),"invalid clock releases");
        c.Enabled=false;C(!p.Evaluate(F(12),c),"disabled");c.Enabled=true;
        using(var pe=new PEReader(File.OpenRead("references/sain-4.5.1/SAIN.dll")))
        {
            var md=pe.GetMetadataReader(); var type=md.TypeDefinitions.Select(md.GetTypeDefinition).Single(t=>md.GetString(t.Name)=="BotLightController");
            var methods=type.GetMethods().Select(md.GetMethodDefinition).Select(x=>md.GetString(x.Name)).ToArray();
            C(methods.Contains("updateLightToggle")&&methods.Contains("ManualUpdate")&&methods.Contains("ToggleLight"),"original SAIN actuator contract");
            C(type.GetFields().Select(md.GetFieldDefinition).Any(x=>md.GetString(x.Name)=="wantLightOn"),"native preference exists and is never overwritten");
        }
        var folder=Path.Combine(Path.GetTempPath(),"comet-l01-config-"+Guid.NewGuid().ToString("N"));
        try {
            var store=new Comet.Server.ConfigStore(Path.Combine(folder,"config.json"));var d=store.Read();
            d.LightDiscipline.HiddenDelay=2;d.LightDiscipline.Enabled=false;var saved=store.Save(d,d.Revision);
            C(saved.Revision==2&&!store.Read().LightDiscipline.Enabled,"server persisted toggle and revision");
            var game=JsonConvert.DeserializeObject<CometConfig>(store.ForGame())!;C(game.LightDiscipline.HiddenDelay==2,"server game payload retains light settings");
            d=store.Read();d.LightDiscipline.MaxDistance=-1;bool rejected=false;try{store.Save(d,d.Revision);}catch(ArgumentException){rejected=true;}
            C(rejected&&store.Read().LightDiscipline.MaxDistance==60,"invalid save leaves previous settings intact");
        } finally {if(Directory.Exists(folder))Directory.Delete(folder,true);}
        Console.WriteLine($"{n} L01 policy/config/native-contract checks passed"); return n;
    }
}
