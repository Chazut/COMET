using Comet.Core;

internal static class PlanContinuityTests
{
    internal static int Run()
    {
        int n = 0;
        void Check(bool b, string message)
        {
            n++;
            if (!b)
                throw new Exception("Plan continuity: " + message);
        }
        var lease = new PlanContinuity();
        for (int i = 0; i <= 600; i++)
            Check(
                lease.Keep(10 + i * .001, .5, "target:Shoot", true) == (i < 500),
                "repeated callbacks never extend deadline"
            );
        lease.Clear();
        Check(lease.Keep(20, .5, "target:Shoot", true), "healthy observations reset lease");
        Check(!lease.Keep(20.1, .5, "target:Shoot", false), "invalid action cancels");
        Check(!lease.Keep(double.NaN, .5, "x", true), "invalid clock cancels");
        Check(!lease.Keep(21, 0, "x", true), "zero tolerance");
        VisibleSituation Frame() =>
            new()
            {
                Contact = new()
                {
                    Now = 10,
                    Target = "enemy",
                    IsAi = true,
                    IsSupportedBot = true,
                    IsAuthority = true,
                    IsActive = true,
                    Visible = true,
                    HasContact = true,
                    KnownDistance = 12,
                    ContactAge = .1,
                    Personality = "Normal",
                },
                WeaponReady = true,
                CanShoot = true,
                ProbeReady = true,
                ThreatCount = 1,
                ThreatSignature = "enemy:visible",
                ContinuationSafe = true,
            };
        CometConfig Config()
        {
            var c = new CometConfig();
            c.VisibleCombat.CoverWeight = 0;
            c.VisibleCombat.PushWeight = 0;
            return c;
        }
        {
            var e = new VisibleEnemyDecision(Config());
            var f = Frame();
            var first = e.Evaluate(f);
            Check(first.Decision == VisibleTactic.Shoot, "visible best option");
            f.ProbeReady = false;
            for (int i = 0; i < 15; i++)
            {
                f.Contact.Now = 10.1 + i * .1;
                var kept = e.Evaluate(f);
                Check(
                    kept.Decision == VisibleTactic.Shoot && !kept.Changed,
                    "source 2-3s decision interval survives queued diagnostics"
                );
                Check(
                    ReferenceEquals(first.Candidates, kept.Candidates),
                    "no reroll within source interval"
                );
            }
            f.Contact.Now = 13.1;
            Check(e.Evaluate(f).Reconsidered, "source decision interval expires");
            f.Contact.Ghost = true;
            Check(e.Evaluate(f).Decision == null, "Ghost ownership remains outside COMET");
        }
        foreach (
            Action<VisibleSituation> extra in new Action<VisibleSituation>[]
            {
                f => f.ThreatSignature = "new-threat",
                f => f.Contact.Hurt = true,
                f => f.Contact.LowAmmo = true,
                f => f.Contact.Suppressed = true,
                f => f.Contact.Fear = new FearTrace { Panic = true },
                f => f.ContinuationSafe = false,
            }
        )
        {
            var e = new VisibleEnemyDecision(Config());
            var f = Frame();
            var first = e.Evaluate(f);
            f.Contact.Now += .1;
            extra(f);
            Check(
                e.Evaluate(f).Decision == first.Decision,
                "COMET-added evidence gates do not abort source decision"
            );
        }
        foreach (
            Action<VisibleSituation> integration in new Action<VisibleSituation>[]
            {
                f => f.Contact.IsAuthority = false,
                f => f.Contact.IsActive = false,
                f => f.Contact.Ghost = true,
                f => f.Contact.Visible = false,
            }
        )
        {
            var e = new VisibleEnemyDecision(Config());
            var f = Frame();
            e.Evaluate(f);
            integration(f);
            Check(
                e.Evaluate(f).Decision == null,
                "native/integration scope still releases control"
            );
        }
        return n;
    }
}
