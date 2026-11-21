using System;
using System.Linq;
using Comet.Core;

// Behavioral contract: zzap fbb86b83 (see SOURCE_BASELINE.md).
internal static class PostCombatTests
{
    public static int Run()
    {
        int n = 0;
        void Check(bool ok, string why)
        {
            n++;
            if (!ok)
                throw new Exception("PostCombat: " + why);
        }

        var c = new PostCombatConfig();
        var p = new PostCombat();
        p.ObserveFight(0);
        p.ObserveFight(0);
        Check(!p.WantsRecovery(30) && !p.Tick(new() { Now = 30 }, c, 0, 0),
            "a single combat frame cannot arm recovery, even when evaluated much later");
        p.Clear("new-raid");
        p.ObserveFight(7.5);
        p.ObserveFight(9);
        var f = new PostCombatFrame
        {
            Now = 10,
            AmmoPercent = 40,
            NeedsCover = true,
            MedicalUseful = true,
            Regroup = true,
        };
        Check(
            p.Tick(f, c, 0, 0) && p.Trace!.State == "cover" && p.WantsReload,
            "seeking protection and attempting a reload share the first update"
        );
        Check(p.Trace!.CombatLayerSeconds == 1.5, "eligibility records first-to-last combat time");
        p.Complete("reload", "requested");
        f.Reloading = true;
        f.Now = 10.25;
        p.Tick(f, c, 0, 0);
        Check(p.Trace!.State == "cover" && p.Trace.Reloading && !p.WantsReload,
            "keep moving before treatment while the native reload runs");
        p.Complete("cover", "arrived");
        f.NeedsCover = false;
        f.Now = 10.375;
        p.Tick(f, c, 0, 0);
        Check(p.Trace!.State == "regroup" && p.Trace.Reloading && !p.WantsReload,
            "after cover, regroup during the existing reload even when first aid is useful");
        f.Regroup = false;
        f.Now = 10.4375;
        p.Tick(f, c, 0, 0);
        Check(p.Trace!.State == "reload" && p.Trace.Reloading, "a close leader leaves the reload running");
        f.Reloading = false;
        f.Now = 10.5;
        p.Tick(f, c, 0, 0);
        Check(p.Trace!.State == "medical", "then heal");
        f.MedicalBusy = true;
        f.Now = 60;
        p.Tick(f, c, 0, 0);
        Check(
            p.Active && p.Trace!.State == "medical",
            "native medicine may outlast the ordinary recovery budget within the combat window"
        );
        f.MedicalBusy = false;
        f.MedicalUseful = false;
        f.Heard = true;
        f.Coming = true;
        f.Regroup = true;
        f.Now = 60.25;
        p.Tick(f, c, 0, 0);
        Check(
            p.Active && p.Trace!.State == "watch" && !p.Trace.AdvanceSelected,
            "source sound watch prevents leaving or finish"
        );
        f.Heard = false;
        f.Coming = false;
        f.Now = 64;
        Check(!p.Tick(f, c, 0, 0), "finish when recovery work and sound watch are done");
        foreach (double age in new[] { 14d, 14.25, 35 })
        {
            var quiet = new PostCombat();
            quiet.ObserveFight(8);
            quiet.ObserveFight(10);
            var frame = new PostCombatFrame { Now = 10 + age, NeedsCover = true, Regroup = true, CanAdvance = true, AmmoPercent = 5 };
            Check(!quiet.Tick(frame, c, 0, 0) && quiet.Trace == null,
                "a late first poll cannot start ordinary work or initiate a new reload at combat age " + age);
            frame.NeedsCover = false;
            frame.Heard = true;
            frame.Now += .25;
            Check(quiet.Tick(frame, c, 0, 0) && quiet.Trace!.Episode == 1,
                "a new sound can still admit the first episode inside the absolute window");
            frame.Heard = false;
            frame.Now += .25;
            Check(!quiet.Tick(frame, c, 0, 0) && quiet.Trace!.Reason == PostCombat.OrdinaryWindowExpired,
                "the elapsed ordinary window cannot restart after an extension clears");
            quiet.ResetCombatWindow();
            quiet.ObserveFight(81);
            quiet.ObserveFight(83);
            Check(quiet.Tick(new() { Now = 85, Regroup = true }, c, 0, 0) && quiet.Trace!.Remaining == 12,
                "a new fight has its own ordinary deadline");
        }
        foreach (var work in new[] { "sound", "medical-busy", "medical-useful", "medical-pending" })
        foreach (bool reloading in new[] { false, true })
        {
            var policy = new PostCombat();
            policy.ObserveFight(40);
            policy.ObserveFight(42);
            var frame = new PostCombatFrame {
                Heard = work == "sound", MedicalBusy = work == "medical-busy",
                MedicalUseful = work == "medical-useful", Reloading = reloading,
                NativeMedicalWork = work == "medical-pending",
            };
            // Start recovery late. The absolute deadline still falls at 102.
            for (int time = 52; time < 102; time++)
            {
                frame.Now = time;
                Check(policy.Tick(frame, c, 0, 0), "work can keep recovery until the combat deadline: " + work);
                Check(policy.Trace!.Remaining > 0 && policy.Trace.Remaining <= 102 - time,
                    "the active budget includes the extension and never exceeds the deadline: " + work);
            }
            frame.Now = 101.999;
            Check(policy.Tick(frame, c, 0, 0) && policy.WantsRecovery(frame.Now),
                "work is still eligible just below sixty seconds: " + work);
            Check(policy.Trace!.WatchRemaining <= 102 - frame.Now, "watch is clamped to the combat deadline");
            frame.Now = 102;
            Check(!policy.WantsRecovery(frame.Now) && !policy.Tick(frame, c, 0, 0)
                && policy.Trace.Reason == PostCombat.WindowExpired && policy.Trace.SecondsSinceCombat == 60
                && policy.Trace.Remaining == 0 && policy.Trace.WatchRemaining == 0 && !policy.Trace.Applied,
                "the first tick at sixty seconds releases even busy hands or repeated sound: " + work);
            frame.Now = 107;
            Check(!policy.Tick(frame, c, 0, 0), "the same fight cannot restart at 65 seconds: " + work);
            policy.ResetCombatWindow();
            policy.ObserveFight(108);
            policy.ObserveFight(109.5);
            frame.Now = 110;
            Check(policy.Tick(frame, c, 0, 0) && policy.Trace!.Episode == 2, "a new fight gets its own window");
        }
        foreach (var work in new[] { "sound", "medical-busy", "medical-useful", "medical-pending" })
        {
            var policy = new PostCombat();
            policy.ObserveFight(1);
            policy.ObserveFight(3);
            var config = new PostCombatConfig { MedicalDuration = 22 };
            var frame = new PostCombatFrame {
                Now = 18, Heard = work == "sound", MedicalBusy = work == "medical-busy",
                MedicalUseful = work == "medical-useful", Reloading = true,
                NativeMedicalWork = work == "medical-pending",
            };
            Check(policy.Tick(frame, config, 0, 0) && policy.Trace!.Reloading,
                "a genuine extension can admit a reloading bot after the ordinary deadline: " + work);
            Check(policy.Trace!.Remaining == (work == "sound" ? 3 : config.MedicalDuration),
                "a concurrent reload leaves the sound or medical budget intact: " + work);
            frame.Now++;
            frame.Heard = frame.MedicalBusy = frame.MedicalUseful = frame.NativeMedicalWork = false;
            Check(!policy.Tick(frame, config, 0, 0) && policy.Trace!.Reason == PostCombat.OrdinaryWindowExpired
                && policy.Trace.Reloading && policy.Trace.Remaining == 0,
                "release as soon as only the native reload remains: " + work);
        }
        var waiting = new PostCombat();
        waiting.ObserveFight(0);
        waiting.ObserveFight(2);
        var pending = new PostCombatFrame { Now = 16, NativeMedicalWork = true, Regroup = true, CanAdvance = true };
        Check(waiting.Tick(pending, c, 0, 0) && waiting.Trace!.State == "regroup",
            "a pending treatment permits a distant follower to rejoin");
        pending.Now += 8;
        pending.Regroup = false;
        Check(waiting.Tick(pending, c, 0, 0) && waiting.Trace!.State == "check",
            "a pending treatment permits the selected inspection after watching");
        waiting.Complete("check", "destination reached");
        pending.Now++;
        Check(waiting.Tick(pending, c, 0, 0) && waiting.Trace!.State == "watch"
            && waiting.Trace.Reason == "post-combat-waiting-for-treatment", "wait once other work finishes");
        pending.Threat = true;
        Check(!waiting.Tick(pending, c, 0, 0), "returning combat interrupts the medical wait");
        var late = new PostCombat();
        late.ObserveFight(0);
        late.ObserveFight(2);
        Check(!late.Tick(new() { Now = 62, Heard = true }, c, 0, 0) && late.Trace == null,
            "late first evaluation cannot start an episode at the deadline");
        var shorter = new PostCombat();
        shorter.ObserveFight(0);
        shorter.ObserveFight(2);
        Check(shorter.Tick(new() { Now = 3, MedicalBusy = true }, new() { MedicalDuration = 22 }, 0, 0)
            && shorter.Trace!.Remaining == 22, "a shorter configured medical budget is retained in diagnostics");
        p.ResetCombatWindow();
        p.ObserveFight(88.5);
        p.ObserveFight(90);
        f.Now = 91;
        f.Regroup = false;
        f.Threat = false;
        p.Tick(f, c, 0, 0);
        f.Threat = true;
        Check(!p.Tick(f, c, 0, 0), "new fight immediately resumes combat");

        return n;
    }
}
