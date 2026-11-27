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
        return checks;
    }
}
