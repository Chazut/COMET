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
        };
        Check(
            p.Tick(f, c, 0, 0) && p.Trace!.State == "reload",
            "source attempts one reload while seeking protection"
        );
        Check(p.Trace!.CombatLayerSeconds == 1.5, "eligibility records first-to-last combat time");
        p.Complete("reload", "requested");
        f.Now = 10.25;
        p.Tick(f, c, 0, 0);
        Check(p.Trace!.State == "cover", "move to source cover before treatment");
        p.Complete("cover", "arrived");
        f.NeedsCover = false;
        f.Now = 10.5;
        p.Tick(f, c, 0, 0);
        Check(p.Trace!.State == "medical", "then heal");
        f.MedicalBusy = true;
        f.Now = 80;
        p.Tick(f, c, 0, 0);
        Check(
            p.Active && p.Trace!.State == "medical",
            "native animation may outlast nominal recovery window"
        );
        f.MedicalBusy = false;
        f.MedicalUseful = false;
        f.Heard = true;
        f.Coming = true;
        f.Regroup = true;
        f.Now = 80.25;
        p.Tick(f, c, 0, 0);
        Check(
            p.Active && p.Trace!.State == "watch" && !p.Trace.AdvanceSelected,
            "source sound watch prevents leaving or finish"
        );
        f.Heard = false;
        f.Coming = false;
        f.Now = 84;
        Check(!p.Tick(f, c, 0, 0), "finish when recovery work and sound watch are done");
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
