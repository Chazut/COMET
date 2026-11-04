using Comet.Core;
using Comet.Server;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

internal static class DoorEntryTests
{
    public static int Run()
    {
        int n=0;void Check(bool ok,string message){n++;if(!ok)throw new Exception("D15: "+message);}
        void Invalid(Action a){try{a();}catch(ArgumentException){n++;return;}throw new Exception("D15 expected rejection");}
        var c=new DoorEntryConfig();c.Validate();
        Check(c.Enabled&&c.MinimumStamina==35&&c.CrossingLimit==3.5&&c.DoorRetry==30,"native entry defaults");
        foreach(var pair in new[]{("prepared",40d),("rush",50d),("retreat",60d),("search",15d),("move",10d)})Check(c.Chance(pair.Item1)==pair.Item2,"conditional chance "+pair.Item1);
        Check(c.Chance("patrol")==0,"no out-of-combat entry");
        foreach(var p in typeof(DoorEntryConfig).GetProperties().Where(p=>p.PropertyType==typeof(double)))
        foreach(var bad in new[]{double.NaN,double.NegativeInfinity,double.PositiveInfinity,-1d,1000d})
        {var badConfig=new DoorEntryConfig();p.SetValue(badConfig,bad);Invalid(badConfig.Validate);}
        Invalid(()=>new CometConfig{DoorEntry=null!}.Validate());
        Check(JsonSerializer.Deserialize<CometConfig>("{}")!.DoorEntry.CrossingLimit==3.5,"old config gains native defaults");
        var temp=Path.Combine(Path.GetTempPath(),"comet-d15-"+Guid.NewGuid().ToString("N"));
        try
        {
            var store=new ConfigStore(Path.Combine(temp,"config.json"));var current=store.Read();current.DoorEntry.PreparedChance=0;current.DoorEntry.CrossingLimit=4;
            var saved=store.Save(current,current.Revision);Check(store.Read().DoorEntry.PreparedChance==0&&store.Read().DoorEntry.CrossingLimit==4,"persist tweaks");
            saved.DoorEntry.MinimumStamina=double.NaN;Invalid(()=>store.Save(saved,saved.Revision));Check(store.Read().DoorEntry.MinimumStamina==35,"invalid save atomic");
        }finally{if(Directory.Exists(temp))Directory.Delete(temp,true);}
        DoorEntry Start(double deadline=120,string context="prepared",double roll=0)
        {var p=new DoorEntry();Check(p.Begin("door","known",context,100,deadline,roll,c),"eligible opportunity selected");return p;}
        var a=Start();Check(!a.Begin("other","known","rush",100,120,0,c),"no nested entry");
        Check(a.Request(100.5,35,true,c)&&!a.Request(100.6,80,true,c),"one native request at stamina boundary");
        a.Breached(true,101);Check(a.Trace!.BreachObserved&&!a.Trace.DoorOpenObserved&&!a.Trace.Crossed,"breach does not invent arrival");
        a.Opened(false,101.1,c);a.Movement(true,true,101.2);
        Check(a.Active&&a.Trace.State=="interacting"&&!a.Trace.Crossed&&!a.Trace.AnimationFinished,"opening cannot skip the native animation");
        a.Opened(true,102,c);a.Movement(false,true,102.1);Check(a.Trace.AnimationFinished&&!a.Trace.Arrived,"arrival requires crossing");
        a.Opened(false,102.15,c);Check(a.Trace.AnimationFinished,"observed animation completion is not erased by later state changes");
        a.Movement(true,false,102.2);Check(a.Active&&a.Trace.Crossed&&!a.Trace.Arrived,"crossed but destination not reached");
        a.Movement(true,true,102.3);Check(!a.Active&&a.Trace.State=="completed"&&a.Trace.ValidFor==0,"observed completion");
        Check(!a.Begin("door","new-target","rush",110,120,0,c),"retry survives target and context change");
        Check(a.Begin("other-door","known","rush",110,125,0,c),"another door remains eligible");
        a.End("done");Check(a.Begin("door","known","rush",130,145,0,c),"exact retry boundary");
        var declined=new DoorEntry();Check(!declined.Begin("d","known","prepared",100,120,.4,c),"roll threshold exclusive");
        Check(declined.Trace!.Rolled&&declined.Trace.Reason=="entry-roll-declined"&&!declined.Begin("d","known","prepared",100.1,120,0,c),"no per-frame rerolls");
        foreach(var context in new[]{"prepared","rush","retreat","search","move"})
        {
            var off=new DoorEntryConfig();var prop=typeof(DoorEntryConfig).GetProperty(context=="prepared"?"PreparedChance":char.ToUpper(context[0])+context.Substring(1)+"Chance")!;
            prop.SetValue(off,0d);Check(!new DoorEntry().Begin("d","t",context,1,20,0,off),"zero disables "+context);
            prop.SetValue(off,100d);Check(new DoorEntry().Begin("d","t",context,1,20,.999999,off),"100 accepts eligible "+context);
        }
        a=Start();a.Opened(true,100.1,c);Check(!a.Active&&!a.Trace!.Requested&&!a.Trace.BreachObserved&&a.Trace.Reason=="entry-door-opened-by-other","foreign opening is not success");
        a=Start();a.Request(100.1,80,true,c);a.Opened(true,100.2,c);Check(!a.Active&&!a.Trace!.BreachObserved,"request alone does not prove own breach");
        a=Start();a.Breached(true,100.1);Check(!a.Trace!.BreachObserved,"no unsolicited breach observation");
        a=Start();Check(!a.Request(100.1,34.9,true,c)&&!a.Active&&!a.Trace!.Requested,"low stamina ends without request");
        a=Start();Check(!a.Request(100.1,80,false,c)&&!a.Trace!.Requested,"no request against open door");
        a=Start();Check(!a.Tick(103)&&a.Trace!.Reason=="entry-approach-timeout","approach limited to 3 seconds");
        a=Start();a.Request(100.5,80,true,c);a.Breached(false,101);Check(a.Active&&!a.Trace!.BreachObserved,"no kick evidence stays pending");
        Check(a.Tick(108.49)&&!a.Tick(108.5)&&a.Trace.Reason=="entry-native-interaction-timeout","native watchdog bounded at 8 seconds without success");
        a=Start();a.Request(100.1,80,true,c);a.Breached(true,101);a.Opened(true,102,c);
        Check(a.Tick(105.49)&&!a.Tick(105.5)&&a.Trace!.Reason=="entry-crossing-timeout","crossing deadline starts after animation");
        a=Start(102);a.Request(100.1,80,true,c);a.Breached(true,100.5);a.Opened(true,101,c);
        Check(!a.Tick(102)&&a.Deadline==102&&a.Trace!.Reason=="entry-original-budget-ended","crossing never extends parent budget");
        foreach(var reason in new[]{"death","ghost","authority-lost","native-medical","target-changed","native-action-replaced","raid-ended"})
        {a=Start();a.Request(100.1,80,true,c);a.End(reason);a.Breached(true,100.2);a.Opened(true,100.3,c);a.Movement(true,true,100.4);Check(!a.Active&&a.Trace!.Reason==reason&&!a.Trace.Arrived&&!a.Trace.BreachObserved,"interruption cannot revive "+reason);}
        foreach(double bad in new[]{double.NaN,double.PositiveInfinity,double.NegativeInfinity,-1d})
        {
            Check(!new DoorEntry().Begin("d","t","rush",bad,120,0,c),"invalid start clock");
            Check(!new DoorEntry().Begin("d","t","rush",100,bad,0,c),"invalid deadline");
            Check(!new DoorEntry().Begin("d","t","rush",100,120,bad,c),"invalid roll");
            a=Start();Check(!a.Request(100.1,bad,true,c)&&!a.Active,"invalid stamina");
            a=Start();Check(!a.Tick(bad)&&!a.Active,"invalid clock ends");
        }
        a=Start();Check(!a.Tick(99),"clock reversal ends");
        Check(!new DoorEntry().Begin("d","t","rush",100,120,1,c),"roll is below one");
        Check(!new DoorEntry().Begin("d","t","rush",100,120,0,new(){Enabled=false}),"disabled entry");
        var refused=new DoorEntry();Check(refused.Refuse("d","known","rush",100,"entry-native-side-refused",c)&&!refused.Trace!.Rolled,"ineligible is recorded without roll");
        Check(!refused.Refuse("d","known","rush",101,"again",c)&&!refused.Begin("d","known","rush",101,120,0,c),"bounded refusals");
        for(int i=0;i<31;i++)Check(refused.Refuse("other"+i,"known","rush",100,"blocked",c),"bounded history entry");
        Check(!refused.Refuse("overflow","known","rush",100,"blocked",c),"history bounded at 32");
        Check(refused.Refuse("overflow","known","rush",130,"blocked",c),"expired history reclaimed");
        Check(DoorEntry.Budget(100,30,null,c)==114.5,"native SAIN action does not use a stale COMET timestamp");
        Check(DoorEntry.Budget(100,30,106,c)==106,"parent plan caps entry");
        Check(DoorEntry.Budget(100,2,110,c)==102,"contact expiry caps entry");
        Check(DoorEntry.Budget(100,30,99,c)<=100,"expired parent cannot create fresh budget");
        var g=new DoorGeometry{Center=new(0,0,0),Axis=new(1,0,0),Normal=new(0,0,1),HalfWidth=.6};
        Check(DoorEntry.Lane(g,new(0,0,1),new(0,0,-2.5),new(0,0,2)),"native interaction lane");
        Check(DoorEntry.Lane(g,new(0,0,-1),new(0,0,2.5),new(0,0,-2)),"mirrored geometry; native eligibility remains separate");
        Check(!DoorEntry.Lane(g,new(0,0,1),new(0,0,2.5),new(0,0,2)),"no turn back");
        Check(!DoorEntry.Lane(g,new(1,0,1),new(0,0,-2.5),new(0,0,2)),"misaligned approach");
        Check(!DoorEntry.Lane(g,new(0,0,2.8),new(0,0,-2.5),new(0,0,2)),"no shoulder-charge run-up");
        Check(!DoorEntry.Lane(g,new(0,0,1),new(0,2,-2.5),new(0,0,2)),"no cross-floor route");
        using(var pe=new PEReader(File.OpenRead("Client/bin/Release/netstandard2.1/COMET.dll")))
        {
            var md=pe.GetMetadataReader();var plugin=md.TypeDefinitions.Select(h=>md.GetTypeDefinition(h)).Single(t=>md.GetString(t.Name)=="Plugin");
            var blobs=plugin.GetCustomAttributes().Select(h=>System.Text.Encoding.UTF8.GetString(md.GetBlobBytes(md.GetCustomAttribute(h).Value))).ToArray();
            foreach(var id in new[]{"com.tarkin.doordash","com.maschine.DualSideDoorBreach"})Check(!blobs.Any(x=>x.Contains(id)),"no external door dependency "+id);
            var refs=md.AssemblyReferences.Select(h=>md.GetString(md.GetAssemblyReference(h).Name)).ToArray();
            Check(refs.Contains("SAIN")&&!refs.Contains("Fika.Core"),"official SAIN, no custom Fika protocol dependency");
        }
        using(var pe=new PEReader(File.OpenRead("C:/Games/SPT-4.1/EscapeFromTarkov_Data/Managed/Assembly-CSharp.dll")))
        {
            var md=pe.GetMetadataReader();
            TypeDefinition Type(string name)=>md.TypeDefinitions.Select(h=>md.GetTypeDefinition(h)).Single(t=>md.GetString(t.Name)==name&&md.GetString(t.Namespace)=="EFT");
            var breach=Type("BreachDoorState").GetMethods().Select(h=>md.GetMethodDefinition(h)).Single(m=>md.GetString(m.Name)=="ExecuteDoorInteraction");
            var names=breach.GetParameters().Select(h=>md.GetString(md.GetParameter(h).Name)).ToArray();
            Check(names.Contains("user")&&names.Contains("interactive")&&names.Contains("interactionResult"),"native completion hook identifies the actor and door directly");
            Check((breach.Attributes&System.Reflection.MethodAttributes.Virtual)!=0,"native kick execution remains virtual");
            var start=Type("Player").GetMethods().Select(h=>md.GetMethodDefinition(h)).Single(m=>md.GetString(m.Name)=="StartInteraction");
            Check((start.Attributes&System.Reflection.MethodAttributes.Virtual)!=0,"Fika can override the normal native interaction entry point");
        }
        a=Start();a.Request(100.5,80,true,c);a.Breached(true,101);a.Opened(true,102,c);
        var trace=a.Trace!;trace.Network="Fika native StartInteraction (host only)";trace.Source="sain-last-known";
        File.WriteAllText("artifacts/d15-door-entry.json",JsonSerializer.Serialize(new{schema=1,botInstance="instance",doorEntry=trace,doorEntryConfig=c},new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase,WriteIndented=true}));
        Console.WriteLine($"D15: {n} checks passed (native entry policy, animation evidence, interruptions, budgets, geometry, config, dependency metadata)");return n;
    }
}
