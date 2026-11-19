using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using Comet.Core;
using Comet.Server;

static class GunfireTests
{
    public static int Run()
    {
        int n = 0;
        void Check(bool ok, string why)
        {
            if (!ok)
                throw new Exception("P03: " + why);
            n++;
        }
        GunfireFrame Frame(double time = 10, double distance = 150) =>
            new()
            {
                Now = time,
                EmittedAt = time,
                Distance = distance,
                Source = "enemy",
                Owner = "SAIN",
                Personality = "Normal",
            };
        GunfireReaction New() => new(new());
        var cfg = new CometConfig();
        cfg.Validate();
        Check(
            cfg.Gunfire.Enabled
                && cfg.Gunfire.NearDistance == 100
                && cfg.Gunfire.InterestDistance == 250,
            "distance defaults"
        );
        Check(
            cfg.Gunfire.LookDuration == 3
                && cfg.Gunfire.LookInterval == 8
                && cfg.Gunfire.SearchInterval == 60
                && cfg.Gunfire.IncomingMemory == 5,
            "approved timing including revised 3-second look"
        );
        var old = JsonSerializer.Deserialize<CometConfig>("{\"Enabled\":false,\"Revision\":7}")!;
        old.Validate();
        Check(!old.Enabled && old.Gunfire.Enabled && old.Revision == 7, "migration");
        foreach (
            var edit in new Action<GunfireConfig>[]
            {
                c => c.NearDistance = 0,
                c => c.InterestDistance = 99,
                c => c.LookDuration = double.NaN,
                c => c.LookInterval = 2,
                c => c.SearchInterval = double.PositiveInfinity,
                c => c.IncomingMemory = 16,
                c =>
                {
                    c.LookDuration = 8;
                    c.LookInterval = 7;
                },
            }
        )
        {
            var c = new GunfireConfig();
            edit(c);
            try
            {
                c.Validate();
                throw new Exception("Accepted invalid config");
            }
            catch (ArgumentException)
            {
                n++;
            }
        }
        var dir = Path.Combine(Path.GetTempPath(), "comet-p03-" + Guid.NewGuid().ToString("N"));
        var store = new ConfigStore(Path.Combine(dir, "config.json"));
        var saved = store.Read();
        saved.Gunfire.LookDuration = 4;
        saved.Gunfire.AllowDistantSearch = false;
        store.Save(saved, saved.Revision);
        var loaded = store.Read();
        Check(
            loaded.Gunfire.LookDuration == 4 && !loaded.Gunfire.AllowDistantSearch,
            "server save and reload"
        );
        foreach (
            var pair in new[]
            {
                (99.9, "native"),
                (100.0, "native"),
                (100.1, "look"),
                (250.0, "look"),
                (250.1, "ignored"),
            }
        )
            Check(
                New().Evaluate(Frame(distance: pair.Item1)).State == pair.Item2,
                "exact distance boundary " + pair.Item1
            );
        foreach (double d in new[] { 50, 150, 300, 700 })
        {
            var f = Frame(distance: d);
            f.Incoming = true;
            Check(New().Evaluate(f).PassToSain, "incoming fire exception " + d);
            f.Incoming = false;
            f.Engaged = true;
            Check(
                New().Evaluate(f).PassToSain == (d <= 100),
                "current target alone does not bypass distance gate " + d
            );
        }
        foreach (
            var edit in new Action<GunfireFrame>[]
            {
                f => f.Audible = false,
                f => f.Busy = true,
                f => f.CanLook = false,
            }
        )
        {
            var f = Frame();
            edit(f);
            Check(New().Evaluate(f).State == "ignored", "sound/safety guard");
        }
        foreach (
            var edit in new Action<GunfireFrame>[]
            {
                f => f.Scoped = false,
                f => f.Distance = double.NaN,
                f => f.Distance = -1,
                f => f.Now = double.PositiveInfinity,
                f => f.EmittedAt = 11,
            }
        )
        {
            var f = Frame();
            edit(f);
            Check(
                New().Evaluate(f).PassToSain,
                "invalid/out-of-scope leaves native behavior available"
            );
        }
        Check(
            new GunfireReaction(new() { Enabled = false }).Evaluate(Frame()).PassToSain,
            "module OFF is transparent"
        );
        try
        {
            var invalid = new CometConfig { Gunfire = null! };
            invalid.Validate();
            throw new Exception("Missing config accepted");
        }
        catch (ArgumentException)
        {
            n++;
        }
        {
            var log = new GunfireLogThrottle();
            var applied = new GunfireTrace { State = "look", Applied = true };
            var ignored = new GunfireTrace { State = "ignored" };
            Check(
                log.ShouldWrite(10, new GunfireTrace { State = "look" }),
                "initial intention logged"
            );
            Check(log.ShouldWrite(10, applied), "first execution bypasses intention throttle");
            for (int second = 11; second <= 13; second++)
            {
                Check(log.ShouldWrite(second, ignored), "refusal diagnostic emitted");
                Check(log.ShouldWrite(second, applied), "burst cannot starve applied heartbeat");
                Check(
                    !log.ShouldWrite(second + .01, ignored)
                        && !log.ShouldWrite(second + .01, applied),
                    "both event streams bounded"
                );
            }
            Check(
                log.TakeCoalesced() == 6 && log.TakeCoalesced() == 0,
                "coalesced count consumed once"
            );
            Check(
                log.ShouldWrite(13.02, new GunfireTrace { State = "ended" }, true),
                "release immediate"
            );
            Check(
                log.ShouldWrite(13.03, applied),
                "new execution does not inherit stale applied flag"
            );
            Check(
                log.ShouldWrite(9, applied),
                "clock rollback does not suppress all later diagnostics"
            );
        }
        {
            var p = New();
            var t = p.Evaluate(Frame());
            Check(t.State == "look" && t.Remaining == 0, "one-shot look, no steering lease");
            Check(p.Evaluate(Frame(17.99)).State == "ignored", "source eight-second look interval");
            Check(p.Evaluate(Frame(18)).State == "look", "look at exact interval");
            var f = Frame();
            f.NearDistance = 160;
            Check(New().Evaluate(f).PassToSain, "native personality near threshold");
            f = Frame();
            f.EmittedAt = 1;
            Check(New().Evaluate(f).State == "look", "no added queued sound age veto");
        }
        {
            var p = New();
            var f = Frame();
            f.PersonalityChases = true;
            Check(p.Evaluate(f).State == "search", "native chaser may search");
            f = Frame(11);
            f.Source = "other";
            f.PersonalityChases = true;
            Check(
                p.Evaluate(f).State == "look",
                "another shooter cannot bypass shared search timer"
            );
            p.Stop(11, "ghost");
            f = Frame(69.99);
            f.PersonalityChases = true;
            Check(p.Evaluate(f).State != "search", "stop/ghost does not reset search timer");
            p.Stop(69.99, "priority");
            f = Frame(70);
            f.PersonalityChases = true;
            Check(p.Evaluate(f).State == "search", "search eligible after sixty seconds");
        }
        {
            var f = Frame();
            f.PersonalityChases = true;
            f.Owner = "ORBIT";
            f.CanSearch = false;
            var t = New().Evaluate(f);
            Check(
                t.State == "search" && t.PassToSain,
                "source chase decision does not borrow movement ownership veto"
            );
            f.Busy = true;
            Check(New().Evaluate(f).State == "ignored", "ORBIT priority also blocks glance");
            f.Busy = false;
            Check(
                new GunfireReaction(new() { AllowDistantSearch = false }).Evaluate(f).State
                    == "look",
                "search disabled still permits attention"
            );
        }
        Check(
            !GunfireReaction.Recent(10, 0, 5)
                && !GunfireReaction.Recent(10, 11, 5)
                && !GunfireReaction.Recent(10, double.NaN, 5),
            "invalid/sentinel/future threat timestamps"
        );
        Check(
            !GunfireReaction.Recent(10, 5, 5) && GunfireReaction.Recent(10, 5.01, 5),
            "incoming memory boundary"
        );
        // Confirm hooks against the exact official DLL, including the native audibility -> gate -> dispersion sequence.
        using (var pe = new PEReader(File.OpenRead("references/sain-4.5.1/SAIN.dll")))
        {
            var md = pe.GetMetadataReader();
            var type = md
                .TypeDefinitions.Select(md.GetTypeDefinition)
                .Single(t => md.GetString(t.Name) == "HearingSensor");
            var react = type.GetMethods()
                .Select(md.GetMethodDefinition)
                .Single(m => md.GetString(m.Name) == "ReactToHeardSound");
            var calls = CallNames(pe, md, react).ToArray();
            Check(
                Array.IndexOf(calls, "CheckIfSoundHeard")
                    < Array.IndexOf(calls, "ShallChaseGunshot")
                    && Array.IndexOf(calls, "ShallChaseGunshot")
                        < Array.IndexOf(calls, "CalcRandomizedPosition"),
                "native audibility precedes P03, normal dispersion follows"
            );
            Check(calls.Count(x => x == "ShallChaseGunshot") == 1, "single native reaction gate");
            foreach (
                var spec in new[]
                {
                    ("HearingSensor", "ShallChaseGunshot", 1),
                    ("HearingSensor", "ReactToHeardSound", 1),
                    ("SAINSteeringClass", "TickPlayerSteering", 0),
                }
            )
            {
                var t = md
                    .TypeDefinitions.Select(md.GetTypeDefinition)
                    .Single(t => md.GetString(t.Name) == spec.Item1);
                var m = t.GetMethods()
                    .Select(md.GetMethodDefinition)
                    .Single(m => md.GetString(m.Name) == spec.Item2);
                Check(
                    m.GetParameters().Select(md.GetParameter).Count(p => p.SequenceNumber > 0)
                        == spec.Item3,
                    "native contract " + spec.Item2
                );
            }
        }
        var trace = New().Evaluate(Frame());
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(
            new
            {
                feature = "P03",
                stage = "evaluated",
                gunfire = trace,
            },
            Comet.Client.Diagnostics.Json
        );
        using (var doc = JsonDocument.Parse(json))
            Check(
                !doc.RootElement.GetProperty("gunfire").GetProperty("applied").GetBoolean(),
                "JSON distinguishes evaluated and applied"
            );
        Console.WriteLine(
            $"P03: {n} checks passed (gunfire bands, fixed gaze, burst/cooldown, native hooks and configuration)."
        );
        return n;
    }

    private static IEnumerable<string> CallNames(
        PEReader pe,
        MetadataReader md,
        MethodDefinition method
    )
    {
        var code = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!;
        var ops = typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(OpCode))
            .Select(f => (OpCode)f.GetValue(null)!)
            .ToDictionary(o => unchecked((ushort)o.Value));
        for (int i = 0; i < code.Length; )
        {
            ushort token = code[i++];
            if (token == 0xfe)
                token = (ushort)(0xfe00 | code[i++]);
            var op = ops[token];
            if (op == OpCodes.Call || op == OpCodes.Callvirt)
            {
                var h = MetadataTokens.EntityHandle(BitConverter.ToInt32(code, i));
                if (h.Kind == HandleKind.MethodDefinition)
                    yield return md.GetString(
                        md.GetMethodDefinition((MethodDefinitionHandle)h).Name
                    );
                else if (h.Kind == HandleKind.MemberReference)
                    yield return md.GetString(md.GetMemberReference((MemberReferenceHandle)h).Name);
            }
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
