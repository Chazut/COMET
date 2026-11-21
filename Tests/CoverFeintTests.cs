using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using Comet.Core;
using Comet.Server;

internal static class CoverFeintTests
{
    public static int Run()
    {
        int n = 0;
        void Check(bool ok, string why)
        {
            if (!ok)
                throw new Exception("M14: " + why);
            n++;
        }
        void Invalid(Action a)
        {
            try
            {
                a();
            }
            catch (ArgumentException)
            {
                n++;
                return;
            }
            throw new Exception("M14 accepted invalid settings");
        }
        CoverFeintFrame F(double now = 10) =>
            new()
            {
                Now = now,
                Age = 2,
                SightAge = 2,
                Target = "contact",
                Key = "contact:1",
                Known = new(0, 0, 12),
                Position = new(0, 0, 0),
                MagazineAudible = true,
                Outdoor = true,
                Cover = true,
                Ready = true,
                Healthy = true,
                Bold = true,
                Grounded = true,
                Protected = true,
                Distance = 12,
                AmmoRatio = .8,
            };
        var c = new CoverFeintConfig();
        c.Validate();
        CheckMoveOrders();
        void CheckMoveOrders()
        {
            var settings = new CoverFeintConfig { Peeks = 1 };
            CoverFeintPolicy StartLeg(string leg, out float start)
            {
                var policy = new CoverFeintPolicy();
                var frame = F();
                Check(policy.Begin(frame, settings, () => .1), "select a bait peek");
                policy.Accepted(true);
                policy.Plan(new(2, 0, 0), frame.Now);
                start = 10f;
                if (leg == "back")
                {
                    policy.MoveResult(start, true);
                    frame.Now = 10.1f;
                    frame.AtGoal = true;
                    policy.Observe(frame, settings);
                    frame.Now = start = 10.5f;
                    policy.Observe(frame, settings);
                }
                Check(policy.WantsRoute && policy.MoveOrderDue(start), leg + " starts with an order due");
                return policy;
            }

            foreach (var leg in new[] { "out", "back" })
            {
                foreach (bool renewal in new[] { false, true })
                {
                    var policy = StartLeg(leg, out float start);
                    float now = start;
                    if (renewal)
                    {
                        policy.MoveResult(start, true);
                        Check(!policy.MoveOrderDue(start + .6f), leg + " does not renew at exact equality");
                        now = start + 1.3f;
                        policy.Observe(F(now), settings);
                        Check(policy.MoveOrderDue(now) && policy.Trace!.State == leg,
                            "an overdue " + leg + " renewal is resolved before the phase timeout");
                    }
                    int before = policy.Trace!.MoveRequests;
                    policy.MoveResult(now, false);
                    Check(!policy.Observe(F(now + 3), settings) && !policy.MoveOrderDue(now + 3)
                        && policy.Trace.Reason == "feint-no-path" && policy.Trace.LastMoveLeg == leg
                        && policy.Trace.MoveRequests == before + 1 && policy.Trace.CompletedPeeks == 0
                        && !policy.Trace.ReturnedObserved && policy.Trace.SharedCooldownUntil == now + 5d,
                        leg + (renewal ? " renewal" : " initial order") + " refused ends the peek immediately");
                }

                var accepted = StartLeg(leg, out float at);
                accepted.MoveResult(at, true);
                int requests = accepted.Trace!.MoveRequests;
                accepted.MoveResult(at, true);
                Check(accepted.Trace.MoveRequests == requests, "duplicate callback cannot reissue " + leg);
                float renew = at + .65f;
                accepted.Observe(F(renew), settings, actionTick: false);
                Check(accepted.Trace.MoveRequests == requests && accepted.MoveOrderDue(renew),
                    "passive observation leaves the " + leg + " order for the action");
                accepted.MoveResult(renew, true);
                accepted.AdvanceMove(F(renew), settings);
                Check(accepted.Trace.State == leg && accepted.Trace.MoveRequests == requests + 1
                    && !accepted.MoveOrderDue(renew), leg + " renewal keeps the current leg");
                accepted.Observe(F(at + (leg == "out" ? .91f : 1.21f)), settings);
                Check(accepted.Trace.State == (leg == "out" ? "show" : "final-hold"),
                    leg + " renewal preserves the original phase timer");

                var arrived = StartLeg(leg, out at);
                var arrival = F(at);
                arrival.AtGoal = true;
                requests = arrived.Trace!.MoveRequests;
                arrived.Observe(arrival, settings);
                Check(!arrived.MoveOrderDue(at) && arrived.Trace.MoveRequests == requests
                    && arrived.Trace.State == (leg == "out" ? "show" : "final-hold"),
                    "already at the " + leg + " goal requires no native move order");
            }
        }
        var fullPlusChamber = F();
        fullPlusChamber.AmmoRatio = 31f / 30;
        var fullyLoadedFeint = new CoverFeintPolicy();
        Check(fullyLoadedFeint.Begin(fullPlusChamber, new CoverFeintConfig { BaitPeek = false }, () => 0)
            && fullyLoadedFeint.Trace!.Kind == "magazine",
            "a full magazine plus chamber still permits a fake reload");
        CoverFeintTrace ChooseAt25m(double shotAt = -1, bool mate = false, bool coming = false)
        {
            var frame = F(20);
            frame.Distance = 25;
            frame.Fire = shotAt;
            frame.Mates = mate;
            frame.Coming = coming;
            var policy = new CoverFeintPolicy();
            int draw = 0;
            Check(policy.Begin(frame, c, () => draw++ == 0 ? .4 : .1), "both feints pass their own roll");
            return policy.Trace!;
        }
        void CheckPeekTiming()
        {
            var thresholds = new Queue<float>(new[] { .35f, .25f, .2f, 2.2f, 1.5f, .8f, .5f });
            var ranges = new List<(float Min, float Max)>();
            var policy = new CoverFeintPolicy(new RepositionCooldown(), (min, max) =>
            {
                ranges.Add((min, max));
                return thresholds.Dequeue();
            });
            var settings = new CoverFeintConfig();
            var frame = F();
            Check(policy.Begin(frame, settings, () => .1), "timed peek starts");
            policy.Accepted(true);
            policy.Plan(new(1, 0, 0), frame.Now);
            policy.MoveResult(frame.Now, true);
            var trace = policy.Trace!;
            frame.Now = 11;
            frame.AtGoal = true;
            policy.Observe(frame, settings, actionTick: false);
            Check(trace.State == "out", "a passive refresh cannot start the exposure clock");
            policy.Observe(frame, settings);
            Check(trace.State == "show" && ranges.Count == 0, "phase entry does not draw for the next phase");
            frame.Now = 11.25;
            policy.Observe(frame, settings);
            Check(trace.State == "show" && trace.Exposure == .35f, "first exposure draw keeps the bot in place");
            policy.Observe(frame, settings, actionTick: false);
            policy.Observe(frame, settings);
            Check(trace.State == "show" && trace.Exposure == .25f && trace.ExposureChecks == 2,
                "each real execution draws, even at the same time, and equality stays exposed");
            policy.Observe(frame, settings);
            Check(trace.State == "back-pending" && trace.Exposure == .2f && trace.ExposureChecks == 3,
                "a lower draw can end the exposure earlier than the first draw");
            policy.MoveResult(frame.Now, true);
            frame.Now = 12;
            policy.Observe(frame, settings);
            Check(trace.State == "pause" && trace.PauseChecks == 0, "arrival does not draw the between-peek pause");
            frame.Now = 13.5;
            policy.Observe(frame, settings);
            policy.Observe(frame, settings, actionTick: false);
            policy.Observe(frame, settings);
            Check(!trace.PauseThresholdPassed && trace.Pause == 1.5f && trace.PauseChecks == 2,
                "strict pause comparison and passive refresh use the same rules as exposure");
            policy.Observe(frame, settings);
            frame.Now = 14;
            policy.Observe(frame, settings);
            Check(trace.PauseThresholdPassed && trace.PauseChecks == 3 && trace.Pause == .8f,
                "a completed pause is preserved while the geometry check is pending");
            policy.ResolveOppositePeek(null, frame.Now);
            Check(trace.State == "out-pending" && ranges.Count == 6,
                "finishing the delayed probe releases the second peek without another draw");
            policy.MoveResult(frame.Now, true);
            policy.Observe(frame, settings);
            settings.ExposureMin = .4;
            settings.ExposureMax = .5;
            frame.Now = 14.25;
            policy.Observe(frame, settings);
            Check(trace.State == "show" && trace.Exposure == .5f && thresholds.Count == 0
                && ranges.Take(3).All(r => r == (.2f, .35f))
                && ranges.Skip(3).Take(3).All(r => r == (.8f, 2.2f)) && ranges[6] == (.4f, .5f),
                "both outings draw independently using the configured exposure range");
            frame.Visible = true;
            Check(!policy.Observe(frame, settings, actionTick: false) && trace.Reason == "feint-enemy-spotted",
                "a passive refresh can report real sight and release the action without timing draws");
        }
        CheckPeekTiming();
        var neutral = ChooseAt25m();
        var step = ChooseAt25m(coming: true);
        Check(neutral.Kind == "peek" && neutral.MagazineWeight == 1 && !neutral.HeardApproach
            && step.Kind == "magazine" && step.MagazineWeight == 2.5 && step.HeardApproach,
            "a closer footstep changes the first choice at 25 m for the same 0.40 roll");
        var recent = ChooseAt25m(shotAt: 10.001, mate: true);
        Check(recent.PeekWeight == 2.7 && recent.RecentShotAtMe && recent.SquadmateEngaging,
            "a recent directed shot and a supporting squadmate both favor a peek");
        var oldShot = ChooseAt25m(shotAt: 10);
        Check(oldShot.PeekWeight == 1 && !oldShot.RecentShotAtMe, "shot exactly ten seconds old has expired");
        Check(CoverFeintPolicy.MateEngaging("A", "A", true, 30)
            && CoverFeintPolicy.MateEngaging("A", "A", false, 4.999)
            && !CoverFeintPolicy.MateEngaging("A", "A", false, 5)
            && !CoverFeintPolicy.MateEngaging("A", "B", true, 0),
            "only squadmates on the same target qualify, including brief sight loss");
        Check(
            c.Enabled
                && c.BaitPeek
                && c.MagazineCheck
                && c.Jump
                && c.PeekChance == 35
                && c.MagazineChance == 30
                && c.MinDistance == 5
                && c.MaxDistance == 40
                && c.MagazineDistance == 20
                && c.SightLoss == 2
                && c.Memory == 30
                && c.MinStep == .9
                && c.MaxStep == 2
                && c.ExposureMin == .2
                && c.ExposureMax == .35
                && c.Peeks == 2
                && c.HoldMin == 4
                && c.HoldMax == 7
                && c.RetryDelay == 20,
            "approved defaults"
        );
        var wire = new List<JsonElement>();
        static float LowThreshold(float min, float max) => min + .1f * (max - min);
        CoverFeintPolicy p = new(new RepositionCooldown(), LowThreshold);
        void Save() =>
            wire.Add(
                JsonSerializer.Deserialize<JsonElement>(
                    Newtonsoft.Json.JsonConvert.SerializeObject(
                        p.Trace,
                        Comet.Client.Diagnostics.Json
                    )
                )
            );
        int rolls = 0;
        double Low()
        {
            rolls++;
            return .1;
        }
        var deferred = new CoverFeintPolicy();
        deferred.Begin(F(), c, Low);
        deferred.DeferSafety(F(10.1), c, -1000);
        Check(
            deferred.Active
                && !deferred.Moving
                && !deferred.WantsRoute
                && deferred.Trace!.MoveRequests == 0
                && deferred.Trace.Reason == "feint-waiting-for-safety-budget",
            "first probe waits without inventing safety or moving"
        );
        deferred.DeferSafety(F(11.99), c, -1000);
        Check(deferred.Active, "bounded first-probe grace");
        deferred.DeferSafety(F(12), c, -1000);
        Check(
            deferred.Active && deferred.Trace!.Reason == "feint-waiting-for-safety-budget",
            "scheduler deferral does not invent a source action failure"
        );
        deferred = new();
        deferred.Begin(F(), c, Low);
        var danger = F(10.1);
        danger.Visible = true;
        deferred.DeferSafety(danger, c, -1000);
        Check(deferred.Active, "scheduler deferral alone cannot cancel a source plan");
        deferred = new();
        deferred.Begin(F(), c, Low);
        deferred.DeferSafety(F(10.1), c, 10);
        Check(deferred.Active, "brief deferral with recent proof");
        deferred.DeferSafety(F(10.76), c, 10);
        Check(deferred.Active, "scheduling deferral does not add an execution expiry");
        deferred = new();
        deferred.Begin(F(), c, Low);
        deferred.DeferSafety(F(9), c, -1000);
        Check(!deferred.Active, "reversed clock fails closed");
        deferred = new();
        deferred.Begin(F(), c, Low);
        deferred.DeferSafety(F(10.1), c, -1000);
        Check(
            deferred.Observe(F(10.2), c) && deferred.Plan(new(1, 0, 0), 10.2),
            "validated plan can continue after temporary deferral"
        );
        rolls = 0;
        Check(p.Begin(F(), c, Low) && p.Trace!.Kind == "peek", "peek selected once");
        Save();
        Check(rolls == 4 && !p.Begin(F(10.1), c, Low) && rolls == 4, "active tactic never rerolls");
        p.Accepted(true);
        Save();
        Check(p.Plan(new(1, 0, 0), 10.1) && p.TargetPeekPose == .55f
            && p.Trace!.FirstPeekCrouched == true && rolls == 5, "one stance roll on the first usable point");
        Save();
        p.MoveResult(10.2, true);
        Save();
        var f = F(10.4);
        f.Position = new(.5, 0, 0);
        f.Speed = 1;
        Check(
            p.Observe(f, c) && p.Trace!.MovementObserved && !p.Trace.ExposedObserved,
            "movement is not exposure"
        );
        Save();
        f = F(10.5);
        f.Position = new(1, 0, 0);
        f.AtGoal = true;
        f.Speed = 1;
        f.Exposed = true;
        Check(
            p.Observe(f, c) && p.Trace!.State == "show",
            "source arrival starts exposure without speed confirmation"
        );
        f.Speed = 0;
        f.Grounded = false;
        Check(
            p.Observe(f, c, actionTick: false) && p.Trace!.State == "show",
            "no extra grounded confirmation after source arrival"
        );
        f.Grounded = true;
        Check(
            p.Observe(f, c, actionTick: false) && p.Trace!.State == "show" && p.Trace.ExposedObserved,
            "actual grounded arrival starts exposure"
        );
        Save();
        f.Now = 10.6;
        p.Observe(f, c);
        Check(
            p.Trace!.State == "show" && p.Trace.ExposureChecks == 1,
            "only the action samples an exposure threshold"
        );
        f.Now = 10.8;
        p.Observe(f, c);
        Check(
            p.Trace.State == "back-pending" && !p.Trace.ReturnedObserved,
            "elapsed exposure asks to return, never teleports"
        );
        Save();
        p.MoveResult(10.8, true);
        f = F(11);
        f.Position = new(.5, 0, 0);
        p.Observe(f, c);
        Save();
        f = F(11.1);
        f.AtGoal = true;
        f.Protected = true;
        p.Observe(f, c);
        Check(
            p.Trace.State == "pause" && p.Trace.CompletedPeeks == 1 && p.Trace.ReturnedObserved,
            "actual protected return counted"
        );
        Save();
        Check(p.OppositePeekProbe.Equals(new DoorPoint(-1, 0, 0)) && p.TargetPeekPose == null,
            "the first return offers the mirrored point before the pause expires");
        var oppositeHit = new DoorPoint(-1.1, .12, .05);
        p.ResolveOppositePeek(oppositeHit, f.Now);
        p.ResolveOppositePeek(null, f.Now);
        Check(p.Trace.State == "pause" && !p.WantsRoute && p.OppositePeekProbe == null
            && p.Goal.Equals(oppositeHit), "probe once, use the sampled point, wait for the pause");
        f.Now = 12.1;
        p.Observe(f, c);
        Check(p.Trace.State == "out-pending" && p.Trace.UsedOppositePoint == true
            && p.TargetPeekPose == 1f && rolls == 5, "second outward leg stands without another draw");
        Save();
        Check(wire[^1].GetProperty("usedOppositePoint").GetBoolean()
            && wire[^1].GetProperty("firstPeekCrouched").GetBoolean()
            && wire[^1].GetProperty("firstPeekPoint").GetProperty("x").GetDouble() == 1,
            "M14 records the first opening, stance and opposite-side result");
        p.MoveResult(12.1, true);
        f = F(12.3);
        f.Position = new(-1, 0, 0);
        f.Exposed = f.AtGoal = true;
        p.Observe(f, c);
        f.Now = 12.6;
        p.Observe(f, c);
        p.MoveResult(12.6, true);
        f = F(12.9);
        f.AtGoal = true;
        Check(
            p.Observe(f, c)
                && p.Trace.CompletedPeeks == 2
                && p.Trace.ReturnedObserved
                && p.Trace.State == "final-hold",
            "two returns enter source final hold"
        );
        f.Now = 15.5;
        Check(!p.Observe(f, c), "source final hold finishes after 2.5s");
        Save();
        Check(wire[^1].GetProperty("exposureChecks").GetInt32() == 3
            && wire[^1].GetProperty("pauseChecks").GetInt32() == 1,
            "the final trace retains checks from both peeks and the single pause");
        Check(
            p.Begin(F(40), c, Low),
            "source permits same encounter after retry interval without new evidence"
        );
        p.End("test");
        f = F(41);
        f.Fire = 40;
        Check(!p.Begin(f, c, Low), "fresh cue cannot bypass delay");
        p = new();
        int call = 0;
        double MagRoll() => call++ == 0 ? .99 : .1;
        Check(
            p.Begin(F(), c, MagRoll) && p.Trace!.Kind == "magazine",
            "weighted order selects at most one eligible feint"
        );
        p.Accepted(true);
        p.Plan(p.Home, 10.1);
        var corner = new DoorPoint(4, 2, 7);
        p.RequestMagazineCheck(corner);
        p.RequestMagazineCheck(new(8, 3, 10));
        Check(p.LookPoint.Equals(corner) && p.Known.Equals(F().Known) && p.Goal.Equals(p.Home),
            "magazine start snapshots the visible path point once and preserves contact and movement goals");
        Save();
        Check(wire[^1].GetProperty("magazineVisiblePathPoint").GetProperty("x").GetDouble() == corner.X,
            "the actual magazine snapshot is recorded in the M14 trace");
        p.MagazineResult(true, false, F(10.2), c);
        Check(!p.Trace!.AnimationObserved, "accepted check does not prove animation");
        Save();
        p.MagazineObserved(false, true, 10.3);
        Check(p.Trace.State == "mag-check", "no observed operation cannot start ready hold");
        p.MagazineObserved(true, false, 10.4);
        p.Sound("check-magazine", false);
        Check(
            p.Trace.SoundEvents == 1 && p.Trace.PlaybackSubmissions == 0,
            "sound event is not playback"
        );
        p.Sound("", true);
        p.MagazineObserved(false, false, 11);
        Check(p.Trace.State == "mag-check", "animation completion still needs weapon readiness");
        Save();
        p.MagazineObserved(false, true, 12.1);
        Check(p.Trace.State == "hold" && p.Trace.WeaponReady, "ready weapon begins 4-7 s hold");
        var updatedContact = F(12.2);
        updatedContact.Known = new(9, 4, 14);
        p.Observe(updatedContact, c);
        Check(p.LookPoint.Equals(corner), "a later contact update cannot move the saved hold angle");
        Save();
        f = F(16.2);
        Check(p.Observe(f, c), "hold measured from readiness");
        f.Now = 16.5;
        Check(
            !p.Observe(f, c) && p.Trace.Reason == "feint-hold-finished",
            "bounded ready hold ends"
        );
        Save();
        call = 0;
        Check(p.Begin(F(40), c, MagRoll) && p.LookPoint.Equals(F().Known)
            && p.Trace!.MagazineVisiblePathPoint == null, "a new feint cannot inherit the previous angle");
        p = new();
        call = 0;
        p.Begin(F(), c, MagRoll);
        p.Plan(p.Home, 10);
        p.RequestMagazineCheck(corner);
        p.MagazineResult(false, false, F(10), c);
        Check(p.Active && p.Trace!.State == "planning" && p.Trace.Kind == "peek"
            && p.Trace.FallbackReason == "feint-magazine-refused" && !p.Trace.AnimationObserved
            && p.Trace.MagazineRequested && !p.Trace.MagazineAccepted && p.Trace.SharedCooldownUntil == 15,
            "native refusal leaves one peek attempt within the original choice");
        Check(p.LookPoint.Equals(p.Known), "the fallback peek watches the known enemy position");
        Save();
        p.NoPeekPoint(F(12), c);
        Check(!p.Active && p.Trace!.Reason == "feint-no-peek-point" && p.Trace.SharedCooldownUntil == 15,
            "failed alternate search cannot extend the refused check's cooldown or loop back");
        Save();

        p = new();
        rolls = 0;
        p.Begin(F(), c, Low);
        p.Accepted(true);
        var original = p.Trace!;
        f = F(11);
        f.AmmoRatio = .5;
        p.NoPeekPoint(f, c);
        Check(ReferenceEquals(original, p.Trace) && original.Accepted && original.Kind == "magazine"
            && original.FallbackReason == "feint-no-peek-point" && original.SharedCooldownUntil == 0
            && rolls == 4 && p.Plan(p.Home, 11),
            "no initial peek point uses the same episode, rolls and native action at exactly half ammo");
        p.NoPeekPoint(f, c);
        p.RequestMagazineCheck(corner);
        p.MagazineResult(true, true, f, c);
        Check(p.LookPoint.Equals(corner), "a magazine check chosen after a failed peek captures its own angle");
        Check(original.State == "mag-check" && original.MagazineAccepted && original.AnimationObserved
            && original.ExposureChecks == 0 && original.PauseChecks == 0,
            "alternate check executes once and ignores a stale peek result");
        Save();

        foreach (var failure in new[] { "no firearm", "native refusal" })
        {
            p = new();
            call = 0;
            p.Begin(F(), c, MagRoll);
            p.Plan(p.Home, 10);
            if (failure == "no firearm") p.NoFirearm(F(11), c);
            else p.MagazineResult(false, false, F(11), c);
            Check(p.Trace!.Kind == "peek" && p.Trace.SharedCooldownUntil == (failure == "no firearm" ? 0 : 16)
                && p.Plan(new(1, 0, 0), 11) && call == 5 && p.TargetPeekPose == .55f,
                "alternate peek can start immediately after " + failure);
            p.End("feint-native-priority", 12);
            Check(p.Trace.SharedCooldownUntil == 17, "real alternate peek starts its own cooldown on exit");
        }

        foreach (var veto in new[] { "ammo", "magazine disabled", "magazine chance",
            "personality", "peek disabled", "peek chance" })
        {
            bool peekFirst = veto is "ammo" or "magazine disabled" or "magazine chance";
            var settings = new CoverFeintConfig();
            p = new();
            call = 0;
            p.Begin(F(), settings, () => call++ == 0 && !peekFirst ? .99 : .1);
            f = F(11);
            if (veto == "ammo") f.AmmoRatio = .499;
            if (veto == "personality") f.Bold = false;
            if (veto == "magazine disabled") settings.MagazineCheck = false;
            if (veto == "peek disabled") settings.BaitPeek = false;
            if (veto == "magazine chance") settings.MagazineChance = 10;
            if (veto == "peek chance") settings.PeekChance = 10;
            if (peekFirst) p.NoPeekPoint(f, settings);
            else { p.Plan(p.Home, 10); p.NoFirearm(f, settings); }
            Check(!p.Active && p.Trace!.FallbackReason == "" && call == 4,
                "alternate respects its own eligibility and roll: " + veto);
        }

        p = new();
        f = F();
        f.Bold = false;
        p.Begin(f, c, Low);
        p.Plan(p.Home, 10);
        p.NoFirearm(F(11), c);
        Check(!p.Active && p.Trace!.SharedCooldownUntil == 0,
            "when the ordered first choice was skipped, its turn is already spent");
        p = new();
        p.Begin(F(), c, Low);
        f = F(11);
        f.Visible = true;
        p.Observe(f, c);
        p.NoPeekPoint(f, c);
        Check(!p.Active && p.Trace!.FallbackReason == "", "new sight cancels both variants");
        Check(!p.ShouldPullBack(12), "seeing a target before selecting a peek point cannot arm retreat");

        CoverFeintPolicy StartedPeek()
        {
            var policy = new CoverFeintPolicy();
            policy.Begin(F(), c, Low);
            policy.Accepted(true, 10);
            policy.Plan(new(1, 0, 0), 10);
            return policy;
        }
        foreach (bool standAndShoot in new[] { false, true })
        {
            p = StartedPeek();
            f = F(11);
            if (standAndShoot) p.Accepted(false, f.Now, true);
            else
            {
                f.Visible = true;
                p.Observe(f, c);
            }
            var ended = p.Trace!;
            Check(!p.Active && ended.Accepted && ended.SharedCooldownUntil == 16 && !ended.PullBackRequested,
                "spotting ends the real peek before any retreat is requested");
            Check(ended.PullBackFrom == 11.9f && ended.PullBackUntil == 13.5f
                && !p.ShouldPullBack(11) && !p.ShouldPullBack(11.899)
                && p.ShouldPullBack(11.9f) && p.ShouldPullBack(13.499) && !p.ShouldPullBack(13.5f),
                "both sight outcomes preserve the initial decision and the exact half-open retreat window");
            Save();
            // Neither repeated callbacks nor reacquiring a different contact should restart the clock.
            var other = F(12);
            other.Target = "other";
            p.Observe(other, c);
            p.Accepted(false, 12, true);
            p.Clear("no goal for one update", 12, preservePullBack: true);
            Check(p.ShouldPullBack(12) && ended.PullBackUntil == 13.5f && !new CoverFeintPolicy().ShouldPullBack(12),
                "pending retreat survives a contact gap, belongs to one bot and cannot be extended");
            p.Clear("ghost", 12);
            Check(!p.ShouldPullBack(12), "authority or scope cleanup discards pending retreat");
        }
        foreach (var interruption in new[] { "feint-under-fire", "feint-other-visible-enemy", "feint-throwing",
            "target-changed", "native-priority", "timeout" })
        {
            p = StartedPeek();
            f = F(interruption == "timeout" ? 40 : 11);
            f.Visible = true;
            if (interruption == "native-priority") p.Accepted(false, f.Now);
            else
            {
                if (interruption == "target-changed") f.Target = "other";
                if (interruption.StartsWith("feint-", StringComparison.Ordinal)) f.Block = interruption;
                p.Observe(f, c);
            }
            Check(!p.Active && p.Trace!.PullBackUntil == 0, "no sight retreat on " + interruption);
        }
        p = new();
        call = 0;
        p.Begin(F(), c, MagRoll);
        p.Plan(p.Home, 10);
        p.MagazineResult(true, true, F(10), c);
        f = F(11);
        f.Visible = true;
        p.Observe(f, c);
        Check(!p.Active && !p.ShouldPullBack(12), "spotting during a fake reload ends it without scheduling retreat");
        foreach (int peeks in new[] { 1, 2 })
        {
            p = new(new RepositionCooldown(), LowThreshold);
            var settings = new CoverFeintConfig { Peeks = peeks };
            int draws = 0;
            f = F();
            f.Position = new(10, 2, 6);
            p.Begin(f, settings, () => ++draws == 5 ? .4 : .1);
            var first = new DoorPoint(11.5, 2.1, 6.5);
            p.Plan(first, 10);
            Check(p.TargetPeekPose == 1f && p.Trace!.FirstPeekCrouched == false,
                "at exactly 40 percent the first peek stands");
            p.MoveResult(10, true);
            f.Now = 11;
            f.AtGoal = true;
            p.Observe(f, settings);
            Check(p.TargetPeekPose == null, "show does not issue an outbound stance");
            f.Now = 12;
            p.Observe(f, settings);
            p.MoveResult(12, true);
            f.Now = 13;
            p.Observe(f, settings);
            if (peeks == 1)
            {
                Check(p.Trace!.State == "final-hold" && p.TargetPeekPose == 1f
                    && p.OppositePeekProbe == null && p.Trace.PauseChecks == 0,
                    "one-peek preset never probes a second opening or samples the pause");
                continue;
            }
            Check(p.OppositePeekProbe.Equals(new DoorPoint(8.5, 2 - (2.1 - 2), 5.5)),
                "the mirror includes forward offset and elevation around the original cover");
            f.Now = 15;
            p.Observe(f, settings);
            Check(!p.WantsRoute && p.OppositePeekProbe.HasValue, "delayed probe retains its original opening");
            p.ResolveOppositePeek(null, f.Now);
            Check(p.WantsRoute && p.Goal.Equals(first) && p.Trace!.UsedOppositePoint == false
                && p.TargetPeekPose == .55f && draws == 5 && p.Trace.FallbackReason == "",
                "a blocked or off-mesh mirror reuses the first opening immediately at the opposite height");
            p.End("test-interruption", f.Now);
            Check(p.TargetPeekPose == null && p.OppositePeekProbe == null, "no pose or probe after release");
            p.Begin(F(50), settings, Low);
            Check(p.Trace!.FirstPeekCrouched == null && p.Trace.FirstPeekPoint == null
                && p.Trace.UsedOppositePoint == null, "a new session starts with a fresh opening and pose");
        }
        p = new();
        call = 0;
        p.Begin(F(), c, MagRoll);
        p.Plan(p.Home, 10);
        p.RequestMagazineCheck(null);
        p.MagazineResult(true, false, F(10), c);
        p.MagazineObserved(false, false, 14.1);
        Check(p.LookPoint.Equals(F().Known) && p.Trace!.MagazineVisiblePathPoint == null,
            "without a visible path point the hold watches the known position");
        Check(
            p.Active && p.Trace!.State == "hold",
            "source uses timed magazine hold without an added animation proof gate"
        );
        Save();
        foreach (
            string scenario in new[]
            {
                "disabled",
                "ghost",
                "client",
                "non-PMC",
                "indoor",
                "visible",
                "old",
                "too-close",
                "far",
                "hands",
                "no-cover",
                "air",
                "nan",
            }
        )
        {
            p = new();
            f = F();
            var cfg = new CoverFeintConfig();
            switch (scenario)
            {
                case "disabled":
                    cfg.Enabled = false;
                    break;
                case "ghost":
                case "client":
                case "non-PMC":
                    f.Block = scenario;
                    break;
                case "indoor":
                    f.Outdoor = false;
                    break;
                case "visible":
                    f.Visible = true;
                    break;
                case "old":
                    f.SightAge = 31;
                    break;
                case "too-close":
                    f.Distance = 4;
                    break;
                case "far":
                    f.Distance = 41;
                    break;
                case "hands":
                    f.Ready = false;
                    break;
                case "no-cover":
                    f.Cover = false;
                    break;
                case "air":
                    f.Grounded = false;
                    break;
                case "nan":
                    f.Known = new(double.NaN, 0, 0);
                    break;
            }
            int before = rolls;
            bool allowed = p.Begin(f, cfg, Low);
            bool sourceAllows = scenario is "indoor" or "hands" or "air";
            Check(
                sourceAllows ? allowed : !allowed && rolls == before,
                "source encounter eligibility: " + scenario
            );
        }
        foreach (
            string scenario in new[]
            {
                "emergency",
                "route",
                "clock",
                "age",
                "visible",
                "stuck",
                "floor",
            }
        )
        {
            p = new();
            p.Begin(F(), c, Low);
            p.Accepted(true);
            p.Plan(new(1, 0, 0), 10);
            p.MoveResult(10, true);
            f = F(10.2);
            switch (scenario)
            {
                case "emergency":
                    f.Block = "urgent";
                    break;
                case "route":
                    f.RouteValid = false;
                    break;
                case "clock":
                    f.Now = 9;
                    break;
                case "age":
                    f.Age = double.NaN;
                    break;
                case "visible":
                    f.Visible = true;
                    break;
                case "stuck":
                    f.Now = 11.2;
                    break;
                case "floor":
                    f.Position = new(1, 1, 0);
                    f.AtGoal = true;
                    break;
            }
            Check(
                p.Observe(f, c) == (scenario is not ("emergency" or "visible")),
                "source sequence termination: " + scenario
            );
        }
        p = new();
        p.Begin(F(), c, Low);
        p.Plan(new(1, 0, 0), 10);
        f = F(10.1);
        f.Visible = true;
        Check(!p.Observe(f, c), "new sight before moving cancels bait");
        p = new();
        f = F();
        f.Healthy = false;
        Check(
            p.Begin(f, c, Low) && p.Trace!.Kind == "peek",
            "source injury lowers peek weight without a hard veto"
        );
        p = new();
        f = F();
        f.AmmoRatio = .2;
        f.Bold = false;
        Check(!p.Begin(f, c, Low), "no eligible feint yields to SAIN");
        p = new();
        Check(
            !p.Begin(F(), c, () => .99) && p.Trace!.Kind == "" && !p.Active,
            "both failed chances mean staying"
        );
        Save();
        for (int i = 0; i < 70; i++)
        {
            f = F(100 + i * 30);
            f.Key = "key-" + i;
            p.Begin(f, c, Low);
            p.End("done");
        }
        Check(p.HistoryCount == 0, "removed encounter evidence ledger retains no history");
        p.Clear("raid-ended");
        Check(p.HistoryCount == 0 && !p.Active, "raid/scope cleanup");
        foreach (
            var prop in typeof(CoverFeintConfig)
                .GetProperties()
                .Where(x => x.PropertyType == typeof(double))
        )
        foreach (double value in new[] { double.NaN, double.PositiveInfinity, -1, 1000 })
        {
            var cfg = new CoverFeintConfig();
            prop.SetValue(cfg, value);
            Invalid(cfg.Validate);
        }
        Invalid(() => new CoverFeintConfig { ExposureMin = .5, ExposureMax = .2 }.Validate());
        Invalid(() => new CoverFeintConfig { MinStep = 2, MaxStep = 1 }.Validate());
        Invalid(() => new CoverFeintConfig { HoldMin = 8, HoldMax = 2 }.Validate());
        Invalid(() => new CoverFeintConfig { Peeks = 3 }.Validate());
        Invalid(() => new CometConfig { CoverFeint = null! }.Validate());
        Check(
            JsonSerializer.Deserialize<CometConfig>("{}")!.CoverFeint.PeekChance == 35,
            "old server configs receive defaults"
        );
        var dir = Path.Combine(Path.GetTempPath(), "comet-m14-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "config.json");
        try
        {
            var store = new ConfigStore(file);
            var cfg = store.Read();
            cfg.CoverFeint.PeekChance = 60;
            store.Save(cfg, cfg.Revision);
            Check(
                new ConfigStore(file).Read().CoverFeint.PeekChance == 60,
                "server setting persists"
            );
            var prior = File.ReadAllText(file);
            cfg = store.Read();
            cfg.CoverFeint.Peeks = 99;
            Invalid(() => store.Save(cfg, cfg.Revision));
            Check(File.ReadAllText(file) == prior, "invalid edit leaves config intact");
        }
        finally
        {
            foreach (var name in new[] { file, file + ".bak" })
                if (File.Exists(name))
                    File.Delete(name);
            Directory.Delete(dir);
        }
        using (var pe = new PEReader(File.OpenRead("references/sain-4.5.1/SAIN.dll")))
        {
            var md = pe.GetMetadataReader();
            var type = md
                .TypeDefinitions.Select(md.GetTypeDefinition)
                .Single(t => md.GetString(t.Name) == "FreezeAction");
            Check(
                type.GetMethods()
                    .Select(md.GetMethodDefinition)
                    .Any(m => md.GetString(m.Name) == "Update"),
                "official Freeze callback exists"
            );
        }
        using (var pe = new PEReader(File.OpenRead("Client/bin/Release/netstandard2.1/COMET.dll")))
        {
            var md = pe.GetMetadataReader();
            var plugin = md
                .TypeDefinitions.Select(md.GetTypeDefinition)
                .Single(t => md.GetString(t.Name) == "Plugin");
            MethodDefinition Method(string name) =>
                plugin
                    .GetMethods()
                    .Select(md.GetMethodDefinition)
                    .Single(m => md.GetString(m.Name) == name);
            foreach (
                var gate in new[]
                {
                    "IsSupportedAi",
                    "get_Authority",
                    "Ghost",
                    "get_BotActive",
                    "get_IsDead",
                    "get_GameEnding",
                    "get_SAINLayersActive",
                }
            )
                Check(
                    Calls(pe, md, Method("FeintBotScope"), gate) == 1,
                    "compiled scope gate " + gate
                );
            Check(Calls(pe, md, Method("FeintScope"), "FeintBotScope") == 1
                && Calls(pe, md, Method("FeintScope"), "AllowsGoal") == 1,
                "active feints and their retreat use the same bot and contact scope");
            Check(
                Calls(pe, md, Method("FeintPathInitialized"), "Remove") == 1,
                "reused path invalidates old generation"
            );
            Check(Calls(pe, md, Method("FeintFrame"), "get_TimeLastShotAtMe") == 1
                && Calls(pe, md, Method("FeintFrame"), "get_TimeLastShotMe") == 0
                && Calls(pe, md, Method("FeintFrame"), "MateEngaging") == 1
                && Calls(pe, md, Method("FeintFrame"), "IsCloserStep") == 1,
                "shipped selector uses directed fire, squad sight and the last personal sound");
            foreach (var method in new[] { "ValidateEntranceActive", "ExecuteEntrance", "TryBeginManeuver", "StormBlock", "FeintFrame" })
                Check(Calls(pe, md, Method(method), "TacticalAmmoPercent") == 1,
                    method + " reads the same chamber-inclusive ammunition count");
            Check(
                Calls(pe, md, Method("RefreshFeint"), "RemainingRelocationRoute") == 0
                    && Calls(pe, md, Method("RefreshFeint"), "FeintRouteClear") == 0,
                "source feint has no added continuous route/exposure validation"
            );
            Check(
                Calls(pe, md, Method("RefreshFeint"), "CheckAmmo") == 1
                    && Calls(pe, md, Method("RefreshFeint"), "get_HandsController") == 1
                    && Calls(pe, md, Method("RefreshFeint"), "NoFirearm") == 1
                    && Calls(pe, md, Method("RefreshFeint"), "MagazineResult") == 1
                    && Calls(pe, md, Method("PlanFeint"), "NoPeekPoint") == 1
                    && Calls(pe, md, Method("MagazineOperation"), "get_CurrentHandsOperation") == 1,
                "native virtual magazine check and observed operation"
            );
            Check(Calls(pe, md, Method("RefreshFeint"), "SampleOppositePeek") == 1
                && Calls(pe, md, Method("RefreshFeint"), "ResolveOppositePeek") == 1
                && Calls(pe, md, Method("SampleOppositePeek"), "SamplePosition") == 1
                && Calls(pe, md, Method("SampleOppositePeek"), "Linecast") == 1
                && Calls(pe, md, Method("SampleOppositePeek"), "Raycast") == 0
                && Calls(pe, md, Method("SampleOppositePeek"), "TryPlanFeintPoint") == 0,
                "the mirror uses a single native NavMesh sample and direct sightline");
            Check(Calls(pe, md, Method("RefreshFeint"), "SetTargetPose") == 1
                && Calls(pe, md, Method("FinishFeint"), "SetTargetPose") == 1,
                "accepted action applies the native pose and releases its lowered stance on exit");
            Check(
                Calls(pe, md, Method("RefreshFeint"), "ControlledJump") == 1,
                "running jumps use M12 authority and physics"
            );
            Check(
                Calls(pe, md, Method("FeintSteering"), "EndShoot") == 0
                    && Calls(pe, md, Method("FeintSteering"), "TryShoot") == 1,
                "source feint preserves visible-target fire and native suppression before its look target"
            );
            Check(
                Calls(pe, md, Method("StopFeintRoutes"), "OwnFeintRoute") == 1,
                "cleanup only touches owned generation"
            );
            Check(Calls(pe, md, Method("MoveFeint"), "WalkToPoint") == 1
                && Calls(pe, md, Method("MoveFeint"), "StopFeintRoutes") == 0
                && Calls(pe, md, Method("RefreshFeint"), "MoveOrderDue") == 1
                && Calls(pe, md, Method("RefreshFeint"), "MoveResult") == 1,
                "shipped action forwards the native move result and permits reuse of the active path");
            foreach (
                var method in new[]
                {
                    "ChooseFeint",
                    "RefreshFeint",
                    "FeintFrame",
                    "PlanFeint",
                    "TryPlanFeintPoint",
                    "MoveFeint",
                    "FeintSteering",
                }
            )
            foreach (
                var forbidden in new[]
                {
                    "Teleport",
                    "SetEnemyVisible",
                    "get_EnemyPosition",
                    "FindObjectsOfType",
                    "Shoot",
                    "Reload",
                    "FastForward",
                }
            )
                Check(
                    Calls(pe, md, Method(method), forbidden) == 0,
                    "no hidden transforms, forced reactions or fake completion: "
                        + method
                        + " "
                        + forbidden
                );
        }
        p = new();
        f = F();
        f.Bold = false;
        f.MagazineAudible = false;
        Check(
            p.Begin(f, c, Low),
            "source magazine feint does not add an audio-asset eligibility veto"
        );
        Directory.CreateDirectory("artifacts");
        File.WriteAllText("artifacts/m14-wire-traces.json", JsonSerializer.Serialize(wire));
        // Shared fixture uses real serialized fields, so RR checks the same contract as COMET logs.
        var active = wire.First(t => t.GetProperty("state").GetString() == "hold");
        File.WriteAllText(
            "artifacts/m14-cover-feint.json",
            JsonSerializer.Serialize(
                new
                {
                    botInstance = "instance",
                    coverFeint = active,
                    coverFeintConfig = c,
                }
            )
        );
        Console.WriteLine($"M14: {n} assertions passed.");
        return n;
    }

    private static int Calls(PEReader pe, MetadataReader md, MethodDefinition method, string name)
    {
        var ops = typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(OpCode))
            .Select(f => (OpCode)f.GetValue(null)!)
            .ToDictionary(o => unchecked((ushort)o.Value));
        var il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!;
        int count = 0;
        for (int i = 0; i < il.Length; )
        {
            ushort value = il[i++];
            if (value == 0xfe)
                value = (ushort)(0xfe00 | il[i++]);
            var op = ops[value];
            if (op == OpCodes.Call || op == OpCodes.Callvirt)
            {
                var h = MetadataTokens.EntityHandle(BitConverter.ToInt32(il, i));
                string called =
                    h.Kind == HandleKind.MethodDefinition
                        ? md.GetString(md.GetMethodDefinition((MethodDefinitionHandle)h).Name)
                    : h.Kind == HandleKind.MemberReference
                        ? md.GetString(md.GetMemberReference((MemberReferenceHandle)h).Name)
                    : "";
                if (called == name)
                    count++;
            }
            i += op.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget
                or OperandType.ShortInlineI
                or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, i),
                _ => 4,
            };
        }
        return count;
    }
}
