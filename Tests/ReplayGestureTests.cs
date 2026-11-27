using Comet.Client;
using Comet.Core;
using Newtonsoft.Json.Linq;

internal static class ReplayGestureTests
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool ok) { checks++; if (!ok) throw new Exception($"Replay gesture check {checks} failed"); }
        ReplayGestures g = default;
        var peek = new CornerPeekTrace { Episode = 3, Peeks = 1 };
        Check(!g.Observe(peek));
        peek.OpenObserved = true;
        peek.LastOpenPeek = 1;
        Check(g.Observe(peek) && !g.Observe(peek));
        peek.Peeks++;
        Check(!g.Observe(peek)); // The previous exposure cannot confirm another peek.
        peek.LastOpenPeek = 2;
        Check(g.Observe(peek));
        var fire = new CornerPrefireTrace { Episode = 2 };
        Check(!g.Observe(fire));
        fire.ActualShots = 2;
        Check(g.Observe(fire) && !g.Observe(fire));
        var jump = new TacticalJumpTrace { Attempt = 1 };
        Check(!g.Observe(jump));
        jump.TakeoffObserved = true;
        jump.SourceFeature = "D02";
        Check(!g.Observe(jump));
        jump.SourceFeature = "M03";
        Check(g.Observe(jump) && !g.Observe(jump));
        var door = new DoorJumpTrace { TakeoffObserved = true };
        Check(g.Observe(door) && !g.Observe(door));
        door.ReturnTakeoffObserved = true;
        Check(g.Observe(door) && !g.Observe(door));
        var step = new DoorStepTrace { State = "observing", Arrived = true };
        Check(!g.Observe(step));
        step.Looking = true;
        Check(g.Observe(step) && !g.Observe(step));
        step.Peek++;
        step.State = "planning";
        Check(!g.Observe(step));
        step.State = "observing";
        Check(g.Observe(step));
        var feint = new CoverFeintTrace { Kind = "magazine", Episode = 3, MagazineAccepted = true };
        Check(g.Observe(feint) == null);
        feint.AnimationObserved = true;
        Check(g.Observe(feint) == "FakeReload" && g.Observe(feint) == null);
        feint.Kind = "peek";
        feint.ExposedObserved = feint.MovementObserved = true;
        Check(g.Observe(feint) == "CoverFeint" && g.Observe(feint) == null);
        var fake = new DoorFakeTrace { State = "completed", HandsObserved = true };
        Check(g.Observe(fake) && !g.Observe(fake));
        fake = new DoorFakeTrace { State = "completed", HandsObserved = true, Medical = new DoorMedicalTrace { ConsumptionObserved = true } };
        Check(!g.Observe(fake));
        fake.Medical.ConsumptionObserved = false;
        fake.Medical.MedicalHandoff = true;
        Check(!g.Observe(fake));
        fake.Medical.MedicalHandoff = false;
        Check(g.Observe(fake) && !g.Observe(fake));
        var clear = new DoorClearTrace { MovementObserved = true };
        Check(!g.Observe(clear));
        clear.ThresholdCrossed = true;
        Check(g.Observe(clear) && !g.Observe(clear));
        var weave = new RetreatWeaveTrace { State = "evading", Context = "a", MoveRequested = true, Speed = 2, Yaw = 25, ObservedYaw = 25 };
        Check(!g.Observe(weave, 2));
        weave.Yaw = -25;
        Check(!g.Observe(weave, 2.1)); // a request alone is insufficient
        weave.ObservedYaw = -25;
        Check(g.Observe(weave, 2.2) && !g.Observe(weave, 2.3));
        weave.Yaw = weave.ObservedYaw = 25;
        Check(!g.Observe(weave, 5));
        weave.Context = "b";
        weave.Yaw = weave.ObservedYaw = -25;
        Check(!g.Observe(weave, 5.1));
        ReplayGestures other = default;
        Check(other.Observe(peek));
        var json = EssentialReplay.Gesture("session", 5, "M12", "TacticalJump", 1.25f, "id", "PMC", "instance");
        foreach (bool rr in new[]{false,true}) {
            var compact = EssentialReplay.Project(json, rr);
            Check(compact != null && JToken.DeepEquals(JObject.Parse(compact), JObject.Parse(json)));
            Check(!EssentialReplay.Keep("M12", "evaluated", "TacticalJump", rr));
        }
        var pistol = new PistolSwapTrace { Episode = 1, InHands = true };
        Check(g.Observe(pistol) == null);
        pistol.Ready = true;
        Check(g.Observe(pistol) == "DrewPistolEmpty" && g.Observe(pistol) == null);
        pistol.Episode++; pistol.MainLoaded = 2;
        Check(g.Observe(pistol) == "DrewPistol");
        var primary = new WeaponReturnTrace { Episode = 2, From = "a", To = "b", FromSlot = "Holster", InHands = true, Ready = true };
        Check(g.Observe(primary) == "BackToPrimary" && g.Observe(primary) == null);
        primary.Episode++; primary.Origin = "W05";
        Check(g.Observe(primary) == "SwitchedForRange");
        primary.Episode++; primary.ToSlot = "Holster";
        Check(g.Observe(primary) == null);
        var surgery = new SurgeryInterruptionTrace { Requested = true, HandsReleased = true, WeaponReady = true, SourceRefusal = "enemy-visible" };
        Check(g.Observe(surgery) == null);
        surgery.HadHands = true;
        Check(g.Observe(surgery) == "InterruptedSurgeryThreat" && g.Observe(surgery) == null);
        Check(g.Observe(new SurgeryInterruptionTrace { HadHands = true, Requested = true, WeaponReady = false }) == null);
        Check(g.Observe(new SurgeryInterruptionTrace { HadHands = true, Completed = true }) == "RepairedLimb");
        Check(g.Observe(new SurgeryInterruptionTrace { HadHands = true, Requested = true, Completed = true }) == null);
        var med = new MedicalTrace { State = "requested", Choice = "FirstAid", Need = "heavy-bleed" };
        Check(ReplayGestures.TreatmentInProgress(med) == null && g.Observe(med) == null);
        med.Started = true; med.State = "started";
        Check(ReplayGestures.TreatmentInProgress(med) == "TreatingHeavyBleed");
        med.State = "ended"; med.Reason = "medical-use-ended"; med.TargetBleedCleared = true;
        Check(ReplayGestures.TreatmentInProgress(med) == null && g.Observe(med) == "StoppedHeavyBleed" && g.Observe(med) == null);
        foreach (var (need, outcome, cleared, expected) in new[] {
            ("light-bleed", "health-improved", true, "StoppedLightBleed"),
            ("light-bleed", "health-improved", false, (string?)null),
            ("fracture", "fracture-persists", false, null), ("fracture", "fracture-cleared", false, "TreatedFracture") })
            Check(g.Observe(new MedicalTrace { Started = true, State = "ended", Reason = "medical-use-ended", Need = need, Outcome = outcome, TargetBleedCleared = cleared }) == expected);
        var run = new DoorRunTrace { SprintObserved = true };
        Check(!g.Observe(run)); run.FarArrived = true; Check(g.Observe(run) && !g.Observe(run));
        var room = new DoorClearTrace { Projectile = 33 };
        Check(!g.ThrewIntoRoom(room)); room.ProjectileObserved = true; Check(g.ThrewIntoRoom(room) && !g.ThrewIntoRoom(room));
        var light = new RestLightsTrace { State = "dark", Accepted = true, ObservedOff = true };
        Check(!g.Observe(light)); light.Requested = true; Check(g.Observe(light) && !g.Observe(light));
        light.SwitchRequest++;
        Check(g.Observe(light) && !g.Observe(light));
        Check(ReplayGestures.MovedAlongRoute(1f, 1f));
        Check(!ReplayGestures.MovedAlongRoute(.9f, 1f) && !ReplayGestures.MovedAlongRoute(3f, -1f));
        Check(ReplayGestures.EscapesDiverge(4, 8, -.6f, 6));
        Check(!ReplayGestures.EscapesDiverge(4, 8, 1, 6)); // parallel routes
        Check(!ReplayGestures.EscapesDiverge(4, 3, -.6f, 6)); // closing, not splitting
        Check(!ReplayGestures.EscapesDiverge(4, 8, -.6f, 2)); // common destination
        var move = new RelocationTrace { Episode = 1, Arrived = true };
        Check(!g.Observe(move)); move.MovementObserved = true; Check(g.Observe(move) && !g.Observe(move));
        foreach (string feature in new[] { "W02", "W03", "C07", "D03", "G05", "M13", "H01", "H02", "L03" })
            Check(EssentialReplay.Keep(feature, "performed", "example", false));
        foreach (string stage in new[] { "activity-start", "activity-end" }) {
            var data = EssentialReplay.ActionEvent("raid", 7, "H01", stage, "UsingFirstAid", 42, "bot", "name", "id");
            Check(JToken.DeepEquals(JObject.Parse(data), JObject.Parse(EssentialReplay.Project(data, false)!)));
            Check(!EssentialReplay.Keep("W03", stage, "Switch", false));
        }
        return checks;
    }
}
