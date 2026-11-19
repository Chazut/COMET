using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using Comet.Core;
using Comet.Server;

static class MovementHearingTests
{
    public static int Run()
    {
        int n = 0;
        void Check(bool ok, string why)
        {
            if (!ok)
                throw new Exception("P01: " + why);
            n++;
        }
        var cfg = new CometConfig();
        cfg.Validate();
        Check(
            cfg.MovementHearing.Enabled
                && cfg.MovementHearing.Footsteps
                && cfg.MovementHearing.JumpsAndLandings,
            "enabled defaults"
        );
        Check(
            cfg.MovementHearing.RangeMultiplier == 1 && cfg.MovementHearing.MinimumInterval == .2,
            "approved defaults"
        );
        var old = JsonSerializer.Deserialize<CometConfig>(
            "{\"Schema\":1,\"Revision\":7,\"Enabled\":false}"
        )!;
        old.Validate();
        Check(
            !old.Enabled && old.Revision == 7 && old.MovementHearing.Enabled,
            "old config migrates without globally enabling COMET"
        );
        foreach (double bad in new[] { double.NaN, double.PositiveInfinity, -1, 0, .01, 3 })
        {
            cfg = new();
            cfg.MovementHearing.RangeMultiplier = bad;
            try
            {
                cfg.Validate();
                throw new Exception("invalid range accepted");
            }
            catch (ArgumentException)
            {
                n++;
            }
            cfg = new();
            cfg.MovementHearing.MinimumInterval = bad;
            try
            {
                cfg.Validate();
                throw new Exception("invalid interval accepted");
            }
            catch (ArgumentException)
            {
                n++;
            }
        }
        cfg = new();
        cfg.MovementHearing = null!;
        try
        {
            cfg.Validate();
            throw new Exception("null accepted");
        }
        catch (ArgumentException)
        {
            n++;
        }
        var root = Path.Combine(Path.GetTempPath(), "comet-p01-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new ConfigStore(Path.Combine(root, "config.json"));
        var current = store.Read();
        current.MovementHearing.Footsteps = false;
        current.MovementHearing.MinimumInterval = .4;
        store.Save(current, current.Revision);
        var saved = store.Read();
        Check(
            !saved.MovementHearing.Footsteps
                && saved.MovementHearing.JumpsAndLandings
                && saved.MovementHearing.MinimumInterval == .4,
            "server save/reload"
        );
        var gate = new MovementHearingGate();
        bool Add(string key, double now, double emitted, bool added = true)
        {
            bool ok = gate.Allows(key, now, emitted, .2, added, out _);
            if (ok)
                gate.Record(key, now, emitted, added);
            return ok;
        }
        Check(Add("pmc/source/step", 10, 10), "first actual step");
        Check(!Add("pmc/source/step", 10.01, 10), "second hook blocked");
        Check(
            !Add("pmc/source/step", 10.08, 10.02, false),
            "other mod after COMET: no second roll"
        );
        Check(Add("pmc/source/step", 10.2, 10.2), "next step allowed");
        Check(
            Add("pmc/source/jump", 10.21, 10.21) && Add("pmc/source/land", 10.22, 10.22),
            "landing independent from takeoff"
        );
        Check(
            Add("other-pmc/source/step", 10.22, 10.22)
                && Add("pmc/other-source/step", 10.23, 10.23),
            "other observers and sources independent"
        );
        gate.Clear();
        Check(
            Add("pair", 20.1, 20, false) && !Add("pair", 20.15, 20.01),
            "native first blocks added duplicate"
        );
        Check(Add("pair", 20.16, 20.02, false), "native-only sequence preserved");
        Check(!Add("old", 25, 20) && !Add("future", 20, 25), "old/future signals rejected");
        Check(
            !Add("nan", double.NaN, 20) && !Add("nan", 20, double.NaN),
            "invalid clocks rejected"
        );
        gate.Clear();
        Check(Add("pair", 30, 30) && !Add("pair", 30.1, 29.99), "out of order duplicate rejected");
        gate.Clear();
        for (int i = 0; i < MovementHearingGate.Capacity; i++)
            Add("pair" + i, 40, 40);
        Check(
            gate.Count == MovementHearingGate.Capacity && !Add("overflow", 40, 40),
            "bounded memory"
        );
        gate.Prune(43);
        Check(gate.Count == 0 && Add("overflow", 43, 43), "references age out");
        gate.Clear();
        Check(gate.Count == 0 && Add("pair", 0, 0), "new raid reset");
        Check(
            MovementHearingGate.Fresh(1, 0) && !MovementHearingGate.Fresh(1.001, 0),
            "exact expiration boundary"
        );
        Check(
            MovementHearingScope.Source(true, true, false),
            "awake AI emits regardless of faction"
        );
        Check(!MovementHearingScope.Source(false, true, false), "human source untouched");
        Check(
            !MovementHearingScope.Source(true, true, true)
                && !MovementHearingScope.Source(true, false, false),
            "ghost/dead source silent"
        );
        Check(
            MovementHearingScope.Listener(true, true, true, false, false),
            "host-controlled awake PMC listens"
        );
        foreach (var role in new[] { "Scav", "boss", "guard", "Rogue", "human-PMC" })
            Check(
                !MovementHearingScope.Listener(true, false, true, false, false),
                role + " receives no added hearing"
            );
        Check(
            !MovementHearingScope.Listener(false, true, true, false, false),
            "Fika remote client cannot deliver"
        );
        Check(
            !MovementHearingScope.Listener(true, true, true, false, true),
            "ghost listener excluded"
        );
        Check(
            !MovementHearingScope.Listener(true, true, false, false, false)
                && !MovementHearingScope.Listener(true, true, true, true, false),
            "dead/inactive/raid-ending listener excluded"
        );
        // Real EFT IL: observe actual volume at exactly one common call per path.
        using var pe = new PEReader(
            File.OpenRead(@"C:\Games\SPT-4.1\EscapeFromTarkov_Data\Managed\Assembly-CSharp.dll")
        );
        var md = pe.GetMetadataReader();
        var player = md
            .TypeDefinitions.Select(md.GetTypeDefinition)
            .Single(t => md.GetString(t.Namespace) == "EFT" && md.GetString(t.Name) == "Player");
        var itf = md
            .TypeDefinitions.Select(md.GetTypeDefinition)
            .Single(t => md.GetString(t.Name) == "IPlayerStepAudioController");
        var play = itf.GetMethods()
            .Single(h => md.GetString(md.GetMethodDefinition(h).Name) == "Play");
        Check(
            md.GetMethodDefinition(play)
                .GetParameters()
                .Select(md.GetParameter)
                .Count(p => p.SequenceNumber > 0) == 6,
            "real audio signature"
        );
        var paths = new[] { "DefaultPlay", "PlayStepSound" }
            .Select(name =>
                player
                    .GetMethods()
                    .Single(h => md.GetString(md.GetMethodDefinition(h).Name) == name)
            )
            .ToList();
        var factory = player
            .GetMethods()
            .Single(h => md.GetString(md.GetMethodDefinition(h).Name) == "Sprint");
        var constructor = (MethodDefinitionHandle)
            MetadataTokens.EntityHandle(Calls(pe, md.GetMethodDefinition(factory), true).Single());
        var sprint = md.GetTypeDefinition(md.GetMethodDefinition(constructor).GetDeclaringType());
        paths.Add(
            sprint
                .GetMethods()
                .Single(h => md.GetString(md.GetMethodDefinition(h).Name) == "MoveNext")
        );
        foreach (var handle in paths)
            Check(
                Calls(pe, md.GetMethodDefinition(handle))
                    .Count(t => t == MetadataTokens.GetToken(play)) == 1,
                "unique audio call: " + md.GetString(md.GetMethodDefinition(handle).Name)
            );
        Check(
            sprint.GetFields().Select(md.GetFieldDefinition).Count(f => IsPlayer(md, f)) == 1,
            "sprint player owner"
        );
        var landing = player
            .GetMethods()
            .Single(h => md.GetString(md.GetMethodDefinition(h).Name) == "PlayGroundedSound");
        Check(
            Calls(pe, md.GetMethodDefinition(landing)).Contains(MetadataTokens.GetToken(paths[0])),
            "real landing reaches observed call"
        );
        Console.WriteLine(
            $"P01: {n} checks passed (repeat policy, bounded memory, config and actual EFT audio IL)."
        );
        return n;
    }

    private static bool IsPlayer(MetadataReader md, FieldDefinition field)
    {
        var reader = md.GetBlobReader(field.Signature);
        reader.ReadSignatureHeader();
        if (reader.ReadSignatureTypeCode() != SignatureTypeCode.TypeHandle)
            return false;
        var handle = reader.ReadTypeHandle();
        if (handle.Kind != HandleKind.TypeDefinition)
            return false;
        var type = md.GetTypeDefinition((TypeDefinitionHandle)handle);
        return md.GetString(type.Name) == "Player" && md.GetString(type.Namespace) == "EFT";
    }

    private static IEnumerable<int> Calls(
        PEReader pe,
        MethodDefinition method,
        bool constructors = false
    )
    {
        var code = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!;
        var opcodes = typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(OpCode))
            .Select(f => (OpCode)f.GetValue(null)!)
            .ToDictionary(o => unchecked((ushort)o.Value));
        for (int i = 0; i < code.Length; )
        {
            ushort token = code[i++];
            if (token == 0xfe)
                token = (ushort)(0xfe00 | code[i++]);
            var op = opcodes[token];
            if (constructors ? op == OpCodes.Newobj : op == OpCodes.Call || op == OpCodes.Callvirt)
                yield return BitConverter.ToInt32(code, i);
            i += op.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget
                or OperandType.ShortInlineI
                or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(code, i),
                _ => 4,
            };
        }
    }
}
