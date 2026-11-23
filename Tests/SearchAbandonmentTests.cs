using Comet.Core;

static class SearchAbandonmentTests
{
    public static int Run()
    {
        int n = 0;
        void Check(bool x, string s)
        {
            n++;
            if (!x)
                throw new Exception(s);
        }
        var c = new SearchAbandonmentConfig();
        var p = new SearchAbandonment();
        var f = new SearchAbandonmentFrame
        {
            Target = "enemy",
            Now = 20,
            Stamp = 0,
            PersonalSearch = true,
        };
        Check(p.Evaluate(f, c).ForgetRequested, "source reached and silent 20 seconds");
        f.Visible = true;
        Check(!p.Evaluate(f, c).ForgetRequested, "visible kept");
        f.Visible = false;
        f.Now = 19.99;
        Check(!p.Evaluate(f, c).ForgetRequested, "source silence threshold");
        f.Now = 20;
        f.PersonalSearch = false;
        Check(!p.Evaluate(f, c).ForgetRequested, "unvisited place kept");
        Check(
            p.Current!.Reason == "search-place-not-visited" && !p.Current.PlaceVisited,
            "unvisited search is not mislabeled as waiting for silence"
        );
        f.PersonalSearch = true;
        f.Distance = 100;
        f.Unsafe = true;
        f.Active = false;
        f.Sampled = f.Facing = f.ClearView = f.SameFloor = false;
        Check(p.Evaluate(f, c).ForgetRequested, "no COMET geometry inspection or priority veto");
        Check(
            p.ObserveForgotten(f, c, true) && p.RememberedCount == 0,
            "no artificial failed-memory ban"
        );
        f.Stamp = 1;
        Check(!p.Evaluate(f, c).ForgetRequested, "new evidence resets source age");
        var observation = new SearchContactSample { Target = "a", Distance = 12, Silence = 21 };
        var gate = new ContactDiagnosticGate();
        p.EvaluateContact(observation, c, true);
        var first = gate.ReportFor(p, 1, false)!;
        observation.Target = "b";
        p.EvaluateContact(observation, c, true);
        var second = gate.ReportFor(p, 1, false)!;
        Check(first.Target == "a" && second.Target == "b" && first != second, "independent report snapshots");
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100000; i++)
        {
            observation.Target = i % 2 == 0 ? "a" : "b";
            if (p.EvaluateContact(observation, c, true) || gate.ReportFor(p, 2, false) != null)
                throw new Exception("watching contacts do not request a forget or repeat reports");
        }
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(bytes == 0, "no allocations for unreported evaluations: " + bytes);
        Console.WriteLine($"P09: {bytes} bytes for 100000 alternating, throttled evaluations.");
        observation.Visited = true;
        observation.Silence = c.Silence - .00000001;
        Check(!p.EvaluateContact(observation, c, true), "just below the silence threshold");
        observation.Silence = c.Silence;
        Check(p.EvaluateContact(observation, c, true), "exact silence threshold");
        string pending = observation.Target;
        observation.Target = "a";
        p.EvaluateContact(observation, c, true);
        Check(!p.ObserveForgotten(pending, true) && !p.ObserveForgotten("a", false)
            && p.ObserveForgotten("a", true), "native acknowledgement belongs to the current matching request");
        p.Clear();
        Check(gate.ReportFor(p, 3, true) == null && p.Current == null, "clearing releases latest observation");
        var watch = new StalledCoverContact();
        var sniper = new object();
        var other = new object();
        var pos = new DoorPoint(50.57, -46.94, 145.22);
        bool Tick(float now, float stamp = 0, bool idle = true, object? enemy = null, DoorPoint? at = null)
        {
            enemy ??= sniper;
            watch.NativeChoice(enemy, now);
            return watch.ShouldRelease(enemy, now, stamp, at ?? pos, idle);
        }
        for (int i = 0; i < 45; i++) Check(!Tick(i), "keep an ordinary stationary wait");
        Check(Tick(45), "Samiy: release the stale partial-path contact once");
        Check(!watch.ShouldRelease(sniper, 45, 0, pos, true), "no persistent blacklist");
        for (int i = 46; i < 90; i++) Check(!Tick(i, 46), "reacquisition starts a fresh watch");
        Check(!Tick(90, 90), "new sight, sound or squad report resets the watch");
        for (int i = 91; i < 130; i++) Tick(i, 90);
        Check(!Tick(130, 90, false), "an actual movement, shot or angle interrupts the fallback");
        for (int i = 131; i < 161; i++) Check(!Tick(i, 90), "fresh idle period after interruption");
        var moved = new DoorPoint(pos.X + 1, pos.Y, pos.Z);
        Check(!Tick(161, 90, at: moved), "real displacement resets the watch");
        for (int i = 162; i < 202; i++) Tick(i, 90, at: moved);
        watch.NativeChoice(null, 202); // Explicit Freeze, COMET Hold, or another action.
        Check(!watch.ShouldRelease(sniper, 202, 90, moved, true), "intentional angle is never forgotten");
        Check(!Tick(203, 90, at: moved), "no carryover when native SeekCover resumes");
        for (int i = 204; i < 247; i++) Tick(i, 90, at: moved);
        Check(!Tick(247, 90, enemy: other, at: moved), "a different goal cannot inherit the timer");
        Check(!watch.ShouldRelease(sniper, 247, 90, moved, true), "secondary enemy checks cannot consume the watch");
        for (int i = 248; i < 290; i++) Tick(i, 90, enemy: other, at: moved);
        Check(!Tick(310, 90, enemy: other, at: moved), "sleep or a decision gap cannot complete an idle interval");
        for (int i = 311; i < 355; i++) Tick(i, 90, enemy: other, at: moved);
        for (int i = 355; i < 365; i++) watch.NativeChoice(other, i);
        Check(!Tick(365, 90, enemy: other, at: moved), "inactive knowledge checker cannot accumulate idle time");
        return n;
    }
}
