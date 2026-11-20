using System;
using Comet.Core;

internal static class RepositionCooldownTests
{
    private static RelocationFrame Contact(double now, string enemy) => new()
    {
        Now = now, Target = enemy, Key = enemy, Source = "seen", Outdoor = true, InCover = true,
        SightAge = 3, ContactAge = 3, Known = new(0, 0, 25),
    };

    private static CoverFeintFrame Feint(double now, string enemy) => new()
    {
        Now = now, Target = enemy, Key = enemy, Cover = true, SightAge = 3, Age = 3,
        Distance = 25, Bold = true, Healthy = true, AmmoRatio = .8, Ready = true,
    };

    public static int Run()
    {
        int count = 0;
        void Check(bool ok, string message)
        {
            if (!ok) throw new Exception("M13/M14 cooldown: " + message);
            count++;
        }
        var moveConfig = new OutdoorRelocationConfig();
        var peek = new CoverFeintConfig { MagazineCheck = false };
        var mag = new CoverFeintConfig { BaitPeek = false };
        var cover = new RelocationCandidate { Id = "cover", Safe = true, Angle = 45,
            PathLength = 10, Distance = 10, Position = new(10, 0, 0) };
        double Low() => .1;
        double NoRoll() => throw new Exception("A blocked attempt consumed a random draw");

        foreach (var phase in new[] { "throw", "move", "hold" })
        {
            var clock = new RepositionCooldown();
            var move = new OutdoorRelocation(clock);
            var feint = new CoverFeintPolicy(clock);
            move.History.Observe("A", true, false, 0);
            Check(move.Begin(Contact(10, "A"), moveConfig, Low) && move.Select(cover, moveConfig), "plan " + phase);
            if (phase == "throw") move.Throw(true, 11, "native-accepted");
            else move.MoveRequested(11, move.Origin);
            double end = 20;
            if (phase == "hold")
            {
                var atCover = Contact(12, "A");
                atCover.Position = move.Goal;
                Check(move.Observe(atCover, moveConfig) && move.Holding, "arrival begins hold");
                atCover.Now = end;
                Check(!move.Observe(atCover, moveConfig) && move.Trace!.Reason == "relocate-hold-complete",
                    "normal hold expiry ends the real session");
            }
            else move.End("relocate-native-priority", end);
            Check(move.Trace!.SharedCooldownUntil == end + 5, "expiry recorded at actual end of " + phase);
            move.End("duplicate cleanup", end + 3);
            move.History.Observe("B", true, false, 0);
            Check(!move.Begin(Contact(end + 4.999, "B"), moveConfig, NoRoll) && move.History.Eligible("B"),
                "new enemy stays eligible across the wait");
            Check(!feint.Begin(Feint(end + 4.999, "B"), peek, NoRoll), "M13 blocks M14 for this bot");
            Check(new CoverFeintPolicy(new RepositionCooldown()).Begin(Feint(end + 1, "B"), peek, Low),
                "another bot can feint against the same enemy");
            Check(feint.Begin(Feint(end + 5, "B"), peek, Low), "M14 available at the exact deadline");
            Check(!move.Begin(Contact(end + 5, "A"), moveConfig, NoRoll), "M13 retains its per-enemy roll");
        }

        foreach (var kind in new[] { "peek", "magazine", "refused magazine" })
        {
            var clock = new RepositionCooldown();
            var feint = new CoverFeintPolicy(clock);
            var move = new OutdoorRelocation(clock);
            var config = kind == "peek" ? peek : mag;
            Check(feint.Begin(Feint(10, "A"), config, Low) && feint.Plan(new(1, 0, 0), 11), "plan " + kind);
            if (kind == "peek") feint.Accepted(false, 13);
            else feint.MagazineResult(kind == "magazine", false, Feint(13, "A"), config);
            if (feint.Active) feint.Clear("feint-out-of-scope", 13);
            Check(feint.Trace!.SharedCooldownUntil == 18, "M14 starts cooldown for " + kind);
            feint.Clear("duplicate cleanup", 15);
            move.History.Observe("B", true, false, 0);
            Check(!move.Begin(Contact(17.999, "B"), moveConfig, NoRoll), "M14 blocks M13 across targets");
            Check(move.Begin(Contact(18, "B"), moveConfig, Low), "M13 available at exactly five seconds");
        }

        foreach (var phase in new[] { "move roll", "no cover", "cover rejected on recheck",
            "feint roll", "no peek point", "no firearm" })
        {
            var clock = new RepositionCooldown();
            var move = new OutdoorRelocation(clock);
            var feint = new CoverFeintPolicy(clock);
            move.History.Observe("A", true, false, 0);
            if (phase == "move roll") Check(!move.Begin(Contact(10, "A"), moveConfig, () => .99), "move declined");
            else if (phase == "no cover" || phase == "cover rejected on recheck")
            {
                Check(move.Begin(Contact(10, "A"), moveConfig, Low), "initial cover search");
                if (phase == "cover rejected on recheck") move.Select(cover, moveConfig);
                move.End("relocate-no-usable-cover", 11);
            }
            else if (phase == "feint roll") Check(!feint.Begin(Feint(10, "A"), peek, () => .99), "feint declined");
            else
            {
                Check(feint.Begin(Feint(10, "A"), phase == "no firearm" ? mag : peek, Low), "initial feint plan");
                if (phase == "no firearm") feint.Plan(feint.Home, 10.5);
                feint.End("feint-preparation-failed", 11);
            }
            Check(clock.Until == 0, "no five-second cooldown for " + phase);
            move.History.Observe("B", true, false, 0);
            Check(move.Begin(Contact(11, "B"), moveConfig, Low), "next eligible contact can relocate immediately");
        }

        var timers = new RepositionCooldown();
        var completed = new CoverFeintPolicy(timers);
        completed.Begin(Feint(10, "A"), mag, Low);
        completed.Plan(completed.Home, 10);
        completed.MagazineResult(true, true, Feint(11, "A"), mag);
        completed.MagazineObserved(true, true, 13);
        Check(!completed.Observe(Feint(20, "A"), mag) && completed.Trace!.Reason == "feint-hold-finished"
            && timers.Until == 25, "a completed magazine feint starts the wait at hold expiry");
        var afterFeint = new OutdoorRelocation(timers);
        afterFeint.History.Observe("B", true, false, 0);
        Check(!afterFeint.Begin(Contact(24.999, "B"), moveConfig, NoRoll)
            && afterFeint.Begin(Contact(25, "B"), moveConfig, Low), "relocation resumes after a completed feint");

        timers = new RepositionCooldown();
        var timedFeint = new CoverFeintPolicy(timers);
        timedFeint.Begin(Feint(10, "A"), peek, Low);
        timedFeint.Plan(new(1, 0, 0), 11);
        timedFeint.End("feint-under-fire", 12);
        Check(!timedFeint.Begin(Feint(17, "B"), peek, NoRoll), "M14 keeps its separate roll interval");
        Check(timedFeint.Begin(Feint(30, "B"), peek, Low), "M14 available after both timers expire");
        Console.WriteLine($"M13/M14 shared cooldown: {count} checks passed.");
        return count;
    }
}
