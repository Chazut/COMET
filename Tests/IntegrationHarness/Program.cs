using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using Comet.Core;
using HarmonyLib;

var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
var paths=new[]{root+"references/sain-4.5.1",root+"Client/bin/Release/netstandard2.1", "C:/Games/SPT-4.1/EscapeFromTarkov_Data/Managed",
    "C:/Games/SPT-4.1/BepInEx/core","C:/Games/SPT-4.1/BepInEx/plugins","C:/Games/SPT-4.1/BepInEx/plugins/spt"};
AssemblyLoadContext.Default.Resolving+=(_,name)=>{
    var path=paths.Select(p=>Path.Combine(p,name.Name+".dll")).FirstOrDefault(File.Exists);
    return path==null?null:AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
};
Audit.Run(root);

static class Audit
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Run(string root)
    {
        var probe=new Comet.IntegrationProbe.ProbeHarmony();
        Comet.IntegrationProbe.Plugin.Run(probe);
        if(probe.Errors.Count>0)throw new Exception(string.Join("\n",probe.Errors.Distinct()));
        using var manifest=JsonDocument.Parse(File.ReadAllText(root+"Client/obj/fault-boundaries/integration-manifest.json"));
        IntegrationContract.ValidateHooks(probe.Hooks.Select(h=>(h.Target,h.Kind,h.Callback)),manifest.RootElement.GetProperty("RequiredCallbacks").EnumerateArray().Select(v=>v.GetString()!));
        var dir=Path.Combine(root,"artifacts/i01");Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir,"registrations.json"),JsonSerializer.Serialize(probe.Hooks,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"I01 real-DLL registration audit: {probe.Hooks.Count} hooks, {probe.Hooks.Select(h=>h.Target).Distinct().Count()} target methods; {probe.Transpilers} real-IL transpiler applications. No detours installed.");
        // Metadata preparation runs against the actual built client, without JITing Unity
        // callbacks on the .NET test host or executing any game action.
        var client = typeof(Comet.Client.Plugin).Assembly;
        // Check the shipped callbacks, not only helper behavior against stubs.
        var fire = client.GetType("Comet.Client.TacticalSteeringFire",true)!.GetMethod("TryShoot",BindingFlags.Static|BindingFlags.NonPublic)!;
        foreach(var pair in new[]{("CrossfireAction","OnSteeringTicked"),("PostCombatAction","OnSteeringTicked"),("Plugin","ChaseSteering"),("Plugin","FeintSteering")})
        {
            var callback=client.GetType("Comet.Client."+pair.Item1,true)!.GetMethod(pair.Item2,BindingFlags.Static|BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)!;
            if(!Calls(callback).Any(m=>Equals(m,fire)))
                throw new Exception("Steering override omitted native firing: "+pair);
        }
        var fireCalls=Calls(fire).Select(m=>m.Name).ToArray();
        if(!fireCalls.Contains("ShootAnyVisibleEnemies")||!fireCalls.Contains("TrySuppressAnyEnemy"))throw new Exception("Native tactical fire bindings absent");
        Console.WriteLine("Four shipped steering overrides preserve native multi-target firing; suppression bindings verified.");
        RuntimeHelpers.RunClassConstructor(client.GetType("Comet.Client.SourceCoverAccess",true)!.TypeHandle);
        Console.WriteLine("zzap native cover arrival accessors: official SAIN bindings initialized.");
        var warmup = client.GetType("Comet.Client.DiagnosticWarmup", true)!;
        var prepare = warmup.GetMethod("Prepare", BindingFlags.Static | BindingFlags.NonPublic)!;
        var resolver = (Newtonsoft.Json.Serialization.IContractResolver)Activator.CreateInstance(client.GetType("Comet.Client.SnapshotDiagnosticResolver",true)!,true)!;
        var report = prepare.Invoke(null, new object[] { client, resolver, false })!;
        object Read(string name) => report.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(report)!;
        int contracts = (int)Read("Contracts"), failed = (int)Read("Failed");
        var unavailable = ((IEnumerable<string>)Read("Unavailable")).ToArray();
        // EFT's unsealed delegate classes are accepted by Unity Mono, but rejected by
        // CoreCLR. Only these unrelated event-wiring bodies are allowed to be skipped.
        bool KnownHostLimitation(string s) => s.Contains("delegate classes must be sealed")
            && (s.StartsWith("Comet.Client.Plugin.EnsureClearEvents:") || s.StartsWith("Comet.Client.Plugin.ClearRoomEvents:")
                || s.StartsWith("Comet.Client.Plugin+<>c__DisplayClass") && s.Contains(".<EndRaid>b__"));
        if (contracts < 400 || failed != unavailable.Length || unavailable.Any(s => !KnownHostLimitation(s)))
            throw new Exception($"Client warmup incomplete: {contracts} contracts, {failed} failures: " + string.Join("; ", unavailable));
        Console.WriteLine($"Actual client diagnostic metadata: {contracts} contracts, {failed} known CoreCLR/native-delegate limitations; {Read("Milliseconds"):F1} ms before raids. Mono JIT preparation requires the game.");
        int Readers(string name)=>(int)resolver.GetType().GetProperty(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(resolver)!;
        if(Readers("ReadersPrepared")<100 || Readers("ReadersFallback")!=0)throw new Exception("Client payload readers not prepared");
        Console.WriteLine($"Actual client diagnostic readers: {Readers("ReadersPrepared")} compiled, {Readers("ReadersFallback")} fallback; no payload getter executed.");
    }

    // Read the shipped IL directly. Harmony's copier tries to prepare Unity methods
    // on CoreCLR, where native game calls do not have executable managed bodies.
    private static IEnumerable<MethodBase> Calls(MethodInfo method)
    {
        var opcodes=typeof(OpCodes).GetFields(BindingFlags.Public|BindingFlags.Static)
            .Where(f=>f.FieldType==typeof(OpCode)).Select(f=>(OpCode)f.GetValue(null)!)
            .ToDictionary(o=>unchecked((ushort)o.Value));
        var il=method.GetMethodBody()?.GetILAsByteArray()??throw new Exception("No shipped IL: "+method);
        for(int at=0;at<il.Length;)
        {
            ushort code=il[at++];if(code==0xfe)code=(ushort)(0xfe00|il[at++]);
            var op=opcodes[code];int start=at;
            int size=op.OperandType switch {
                OperandType.InlineNone=>0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar=>1,
                OperandType.InlineVar=>2,
                OperandType.InlineI8 or OperandType.InlineR=>8,
                OperandType.InlineSwitch=>4+4*BitConverter.ToInt32(il,at),
                _=>4
            };
            at+=size;if(at>il.Length)throw new Exception("Invalid shipped IL: "+method);
            if(op==OpCodes.Call||op==OpCodes.Callvirt)
                yield return method.Module.ResolveMethod(BitConverter.ToInt32(il,start),method.DeclaringType?.GetGenericArguments(),method.GetGenericArguments())!;
        }
    }
}

namespace Comet.IntegrationProbe
{
    public sealed class ProbeHook(Type owner,string name)
    {
        public Type Owner=owner;
        public int priority;
        public string[]? before,after;
        public string Name=name;
    }
    public sealed record HookRecord(string Target,string Kind,string Callback);
    public sealed class ProbeHarmony
    {
        public List<HookRecord> Hooks=new();
        public List<string> Errors=new();
        public int Transpilers;
        private readonly Dictionary<(MethodBase,Type),Type> stateTypes=new();
        private readonly Dictionary<(MethodBase,Type),string> stateProducers=new();
        public static void AddLayer(Type type,List<string> brains,int priority)
        {if(!type.IsSubclassOf(typeof(SAIN.Layers.SAINLayer)))throw new Exception("Unknown COMET layer");}
        public void Patch(MethodBase original,ProbeHook? prefix=null,ProbeHook? postfix=null,ProbeHook? transpiler=null,ProbeHook? finalizer=null)
        {
            if(original==null)throw new Exception("Missing native method");
            void Add(ProbeHook? hook,string kind)
            {
                if(hook==null)return;
                var owner=hook.Owner==typeof(Plugin)?typeof(Comet.Client.Plugin):hook.Owner;
                var callback=owner.GetMethod(hook.Name,BindingFlags.Static|BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)
                    ??throw new Exception("Missing callback "+hook.Name);
                CheckSignature(original,callback,kind);
                if(kind=="transpiler")
                {
                    TranspilerAudit.Check(original,typeof(Plugin).GetMethod(hook.Name,BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic)!);
                    Transpilers++;
                }
                if(callback.GetParameters().FirstOrDefault(p=>p.Name=="__state") is {} state)
                {
                    if(kind=="prefix" && state.ParameterType.IsByRef)
                    {
                        if(stateProducers.TryGetValue((original,owner),out var producer)&&producer!=hook.Name)
                            Errors.Add($"Shared Harmony __state producer on {original}: {producer} / {hook.Name}");
                        stateProducers[(original,owner)]=hook.Name;
                    }
                    var stateType=state.ParameterType.IsByRef?state.ParameterType.GetElementType()!:state.ParameterType;
                    if(stateTypes.TryGetValue((original,owner),out var prior)&&prior!=stateType)
                        Errors.Add($"Conflicting Harmony __state on {original}: {prior} / {stateType}");
                    stateTypes[(original,owner)]=stateType;
                }
                Hooks.Add(new(original.DeclaringType!.FullName+"."+original.Name+"("+string.Join(",",original.GetParameters().Select(p=>p.ParameterType.FullName))+")",kind,owner==typeof(Comet.Client.Plugin)?hook.Name:owner.FullName+"."+hook.Name));
            }
            Add(prefix,"prefix");Add(postfix,"postfix");Add(transpiler,"transpiler");Add(finalizer,"finalizer");
        }
        private static void CheckSignature(MethodBase original,MethodInfo hook,string kind)
        {
            if(kind=="transpiler")return;
            Type Element(Type t)=>t.IsByRef?t.GetElementType()!:t;
            foreach(var p in hook.GetParameters())
            {
                string name=p.Name!;
                Type? source=null;
                if(name=="__instance")source=original.DeclaringType;
                else if(name=="__result")source=(original as MethodInfo)?.ReturnType;
                else if(name.StartsWith("___"))source=ProbeAccess.Field(original.DeclaringType,name.Substring(3))?.FieldType;
                else if(name.StartsWith("__")&&int.TryParse(name.Substring(2),out int index))source=original.GetParameters().ElementAtOrDefault(index)?.ParameterType;
                else if(name.StartsWith("__"))continue; // Harmony state, exception, original method and runOriginal.
                else source=original.GetParameters().SingleOrDefault(v=>v.Name==name)?.ParameterType;
                if(source==null||!Element(p.ParameterType).IsAssignableFrom(Element(source)))
                    throw new Exception($"{hook.Name}: {name} ({p.ParameterType}) cannot bind {original.DeclaringType}.{original.Name} ({source})");
                if(p.ParameterType.IsByRef&&Element(p.ParameterType)!=Element(source))
                    throw new Exception($"{hook.Name}: ref {name} type mismatch");
            }
        }
    }
}
