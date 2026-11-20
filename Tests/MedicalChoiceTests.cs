using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Comet.Core;

internal static class MedicalChoiceTests
{
    public static int Run()
    {
        int n = 0;
        void C(bool ok, string why)
        {
            n++;
            if (!ok)
                throw new Exception("H01: " + why);
        }
        var c = new MedicalChoiceConfig();
        c.Validate();
        C(
            c.Enabled
                && c.Interval == .5
                && c.HitDelay == 2
                && c.EmergencyStimDelay == .5
                && c.StimHealth == .35
                && c.FootstepAge == 5
                && c.FootstepDistance == 30
                && c.SurgeryQuiet == 25
                && c.SurgeryDistance == 40
                && c.RetryDelay == 3,
            "defaults"
        );
        foreach (
            var prop in typeof(MedicalChoiceConfig)
                .GetProperties()
                .Where(p => p.PropertyType == typeof(double))
        )
        foreach (
            var value in new[]
            {
                double.NaN,
                double.PositiveInfinity,
                double.NegativeInfinity,
                -1d,
                100001d,
            }
        )
        {
            var invalid = new MedicalChoiceConfig();
            prop.SetValue(invalid, value);
            try
            {
                invalid.Validate();
                C(false, "invalid " + prop.Name);
            }
            catch (ArgumentException)
            {
                n++;
            }
        }
        var config = Newtonsoft.Json.JsonConvert.DeserializeObject<CometConfig>("{}")!;
        config.Validate();
        C(config.MedicalChoice.Enabled, "old configs get defaults");
        config.MedicalChoice = new()
        {
            Enabled = false,
            Interval = 1,
            HitDelay = 3,
            EmergencyStimDelay = 1,
            StimHealth = .5,
            FootstepAge = 8,
            FootstepDistance = 20,
            SurgeryQuiet = 30,
            SurgeryDistance = 50,
            RetryDelay = 5,
        };
        string json = Newtonsoft.Json.JsonConvert.SerializeObject(config);
        var copy = Newtonsoft.Json.JsonConvert.DeserializeObject<CometConfig>(json)!;
        copy.Validate();
        C(
            Newtonsoft.Json.JsonConvert.SerializeObject(copy) == json,
            "server settings persist all values"
        );
        config.MedicalChoice = null!;
        try
        {
            config.Validate();
            C(false, "null accepted");
        }
        catch (ArgumentException)
        {
            n++;
        }
        MedicalFrame Frame() =>
            new()
            {
                Now = 10,
                Contacts = 1,
                Contact = "known",
                X = 50,
                Y = 0,
                Z = 0,
                Nearest = 50,
                Quiet = 30,
                ReachedCover = true,
                ProtectiveCover = true,
                Health = .6,
            };
        MedicalOption Aid(string need = "injury") =>
            new()
            {
                Action = "FirstAid",
                Usable = true,
                Item = "aid",
                Part = "Chest",
                Need = need,
                Duration = 4,
            };
        MedicalOption Stim() =>
            new()
            {
                Action = "Stims",
                Usable = true,
                Item = "stim",
                Part = "Chest",
                Need = "low-health",
                Duration = 2,
            };
        MedicalOption Surg() =>
            new()
            {
                Action = "Surgery",
                Usable = true,
                Item = "kit",
                Part = "LeftLeg",
                Need = "destroyed-limb",
                Duration = 20,
            };
        foreach (var action in new[] { "FirstAid", "Surgery", "Stims" })
        {
            var solid = Frame();
            solid.Health = .3;
            solid.Visible = action == "Stims";
            solid.Options.Add(
                action == "FirstAid" ? Aid()
                : action == "Surgery" ? Surg()
                : Stim()
            );
            var protectedResult = new MedicalChoice().Evaluate(solid, c);
            solid.ProtectiveCover = false;
            var camouflageResult = new MedicalChoice().Evaluate(solid, c);
            double delta =
                protectedResult.Alternatives.Single(x => x.Action == action).Score
                - camouflageResult.Alternatives.Single(x => x.Action == action).Score;
            C(
                Math.Abs(
                    delta
                        - (
                            action == "FirstAid" ? 20
                            : action == "Surgery" ? 15
                            : 35
                        )
                ) < .0001,
                "foliage loses only source protection score: " + action
            );
            C(
                camouflageResult.ReachedCover && !camouflageResult.Cover,
                "arrival and protection remain separately observable"
            );
            C(
                camouflageResult.Alternatives.Single(x => x.Action == action).Usable,
                "no hard medical veto for camouflage: " + action
            );
        }
        var f = Frame();
        f.Options.Add(Surg());
        C(new MedicalChoice().Evaluate(f, c).Choice == "Surgery", "safe surgery");
        foreach (
            var mutation in new Action<MedicalFrame>[]
            {
                x => x.Moving = true,
                x => x.ProtectiveCover = false,
                x => x.ThreatsIncomplete = true,
                x => x.HeavyBleed = true,
                x => x.LightBleed = true,
                x => x.Vital = true,
                x => x.Quiet = 24.99,
            }
        )
        {
            f = Frame();
            f.Options.Add(Surg());
            mutation(f);
            C(
                new MedicalChoice().Evaluate(f, c).Choice == "Surgery",
                "no added hard veto for a source-eligible surgery"
            );
        }
        foreach (
            var mutation in new Action<MedicalFrame>[]
            {
                x => x.Visible = true,
                x => x.Footsteps = true,
                x => x.Priority = "grenade",
            }
        )
        {
            f = Frame();
            f.Options.Add(Surg());
            mutation(f);
            C(
                new MedicalChoice().Evaluate(f, c).Choice == "Wait",
                "source pressure/wait or native urgency wins"
            );
        }
        f = Frame();
        f.Options.Add(Aid());
        f.SinceHit = 1;
        var aid = new MedicalChoice().Evaluate(f, c);
        C(
            aid.Alternatives.Single(x => x.Action == "FirstAid").Score > 0,
            "recent hit penalizes without banning treatment"
        );
        f = Frame();
        f.Options.Add(Stim());
        C(new MedicalChoice().Evaluate(f, c).Choice == "Wait", "healthy bot preserves stimulant");
        f.Health = .3;
        C(new MedicalChoice().Evaluate(f, c).Choice == "Stims", "low-health stimulant");
        f = Frame();
        f.Options.Add(Surg());
        var p = new MedicalChoice();
        var selected = p.Evaluate(f, c);
        C(!selected.Started && !selected.Applied, "selection is not an observed medical operation");
        p.Request(11);
        p.ObserveStart(12);
        C(p.Trace!.Started && p.Trace.Applied, "observed native start");
        f.Options.Clear();
        C(ReferenceEquals(p.Evaluate(f, c), selected), "ongoing native treatment retained");
        p.End(30, "ended", c);
        C(!p.Active && p.Trace.Outcome == "unconfirmed", "no invented completion");
        f.Now = 30.01;
        f.Options.Add(Aid());
        C(p.Evaluate(f, c).Choice == "FirstAid", "no extra COMET treatment cooldown");
        // Reproduce the parity report on the actual treatment scores, then reuse the
        // same policy while the frame changes. A read of the clock cannot consume RNG.
        var settings = new TacticalMistakesConfig();
        int draws = 0;
        double Roll() { draws++; return 0; }
        f = new MedicalFrame { Now = 100, Health = .7, Quiet = 15, Nearest = 25, Meds = 2,
            HasKnownEnemy = true, Personality = "Timmy" };
        f.Options.Add(Aid());
        var medical = new MedicalChoice();
        var wait = medical.Evaluate(f, c, settings, Roll);
        C(wait.NaturalChoice == "FirstAid" && wait.Choice == "Wait" && !medical.Active
            && Math.Round(wait.Alternatives[0].Score, 2) == 49.83 && wait.Alternatives[1].Score == 37
            && wait.MistakeState == "mistake-selected" && wait.MistakeChance == .12 && draws == 1,
            "report: Timmy can hesitate at FirstAid 49.83 vs Wait 37");
        f.Now = 104.99;
        wait = medical.Evaluate(f, c, settings, Roll);
        C(wait.Choice == "Wait" && wait.VerdictUntil == 105 && wait.MistakeRoll == null && draws == 1,
            "wait persists without a redraw or a sliding expiry");
        f.Health = .35;
        f.SinceHit = 1;
        f.Options.Add(Stim());
        var urgent = medical.Evaluate(f, c, settings, Roll);
        C(urgent.Choice == "Stims" && urgent.Urgent && urgent.MistakeState == "urgent-medical-priority"
            && draws == 1, "cached hesitation cannot delay a badly injured bot's stim");
        medical.End(f.Now, "test-finished", c);
        f.Health = .7;
        f.SinceHit = 999;
        f.Options.RemoveAt(1);
        f.Now = 105;
        wait = medical.Evaluate(f, c, settings, () => { draws++; return .5; });
        C(wait.Choice == "FirstAid" && wait.MistakeRoll == .5 && draws == 2, "fresh verdict exactly at five seconds");
        medical.End(f.Now, "test-finished", c);
        f.Now += .1;
        f.SinceHit = 1;
        var risky = medical.Evaluate(f, c, settings, Roll);
        C(risky.NaturalChoice == "Wait" && risky.Choice == "FirstAid" && risky.Item == "aid"
            && risky.Part == "Chest" && draws == 3, "a new best rerolls, and the alternative keeps its kit and part");
        f.SinceHit = 999;
        f.Options.Add(Surg());
        var three = new MedicalChoice().Evaluate(f, c, settings, Roll);
        C(three.Choice == three.Alternatives[2].Action && three.MistakeState == "mistake-selected",
            "third positive treatment is eligible on the source third-place roll");
        f.Options.RemoveAt(1);
        f.ReachedCover = f.ProtectiveCover = true;
        f.Quiet = f.Nearest = 100;
        var far = new MedicalChoice().Evaluate(f, c, settings, Roll);
        C(far.NaturalChoice == far.Choice && far.MistakeState == "score-loss-too-large",
            "a gap above 35 points preserves the best treatment");
        foreach (string mode in new[] { "disabled", "no-contact", "heavy", "vital", "in-use", "no-usable-item" })
        {
            var frame = Frame();
            frame.HasKnownEnemy = mode != "no-contact";
            frame.HeavyBleed = mode == "heavy";
            frame.Vital = mode == "vital";
            if (mode == "in-use") frame.Priority = "medical-already-in-use";
            var item = Aid();
            item.Usable = mode != "no-usable-item";
            frame.Options.Add(item);
            settings.Enabled = mode != "disabled";
            var normal = new MedicalChoice().Evaluate(frame, c, settings,
                () => throw new Exception("unexpected medical roll: " + mode));
            C(normal.Choice == normal.NaturalChoice, "native priority, scope and global setting: " + mode);
        }
        using var pe = new PEReader(
            File.OpenRead(
                Path.GetFullPath(
                    Path.Combine(
                        AppContext.BaseDirectory,
                        "../../../../references/sain-4.5.1/SAIN.dll"
                    )
                )
            )
        );
        var md = pe.GetMetadataReader();
        foreach (
            var (type, method, count) in new[]
            {
                ("SelfActionDecisionClass", "StartBotHeal", 1),
                ("SelfActionDecisionClass", "CheckContinueSelfAction", 2),
                ("SAINSelfActionClass", "DoFirstAid", 0),
                ("SAINSelfActionClass", "DoStims", 0),
                ("SAINSelfActionClass", "DoSurgery", 0),
                ("DoSurgeryAction", "checkDoSurgery", 0),
                ("DoSurgeryAction", "Stop", 0),
            }
        )
        {
            var td = md
                .TypeDefinitions.Select(md.GetTypeDefinition)
                .Single(t => md.GetString(t.Name) == type);
            C(
                td.GetMethods()
                    .Select(md.GetMethodDefinition)
                    .Any(m => md.GetString(m.Name) == method && m.GetParameters().Count == count),
                "pinned native contract " + type + "." + method
            );
        }
        Console.WriteLine($"H01: {n} checks passed");
        return n;
    }
}
