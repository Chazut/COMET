using Comet.Core;

internal static class EnemyRoleAssessmentTests
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool ok, string message)
        {
            checks++;
            if (!ok) throw new Exception("Enemy role assessment: " + message);
        }

        void Classification(string name, ContactRole expectedRole, ContactFaction expectedFaction)
        {
            var id = EnemyIdentity.Identify(name);
            Check(id.Role == expectedRole && id.Faction == expectedFaction, name);
        }

        Classification("bossRook", ContactRole.Boss, ContactFaction.None);
        Classification("followerTombstone", ContactRole.Guard, ContactFaction.None);
        Classification("bossBoarSniper", ContactRole.Guard, ContactFaction.None);
        Classification("marksman", ContactRole.Scav, ContactFaction.None);
        Classification("ruafRiflemanSenior", ContactRole.Soldier, ContactFaction.RUAF);
        Classification("remnantRifleman", ContactRole.Soldier, ContactFaction.RUAF);
        Classification("bossuntarofficer", ContactRole.Boss, ContactFaction.UNTAR);
        Classification("followeruntarmarksman", ContactRole.Guard, ContactFaction.UNTAR);
        Classification("ISBBossFirefly", ContactRole.Boss, ContactFaction.ISB);
        Classification("ISBFollowerCommander", ContactRole.Guard, ContactFaction.ISB);
        Classification("ISBScout", ContactRole.Soldier, ContactFaction.ISB);
        Classification("blackDivIb", ContactRole.Soldier, ContactFaction.BlackDivision);
        Classification("chaosInsurgency", ContactRole.Soldier, ContactFaction.ChaosInsurgency);
        Classification("unregisteredCivilian", ContactRole.Unknown, ContactFaction.None);
        var thirdParty = EnemyIdentity.Identify("watchCaptain", null, new[] { "Watch" });
        Check(thirdParty.Role == ContactRole.Soldier && thirdParty.RegisteredFaction == "Watch", "new MoreBots faction without a hardcoded role");
        Check(EnemyIdentity.Identify("848420", null, new[]{"Black Division"}).Faction == ContactFaction.BlackDivision, "numeric role in faction registry");
        Check(EnemyIdentity.Identify("customMedic", "Followers", new[]{"Watch"}).Role == ContactRole.Guard, "custom guard keeps rank");
        Check(!EnemyIdentity.Identify("assault", null, new[]{"Scavs"}).ExpectsGroup && !EnemyIdentity.Identify("pmcUSEC", null, new[]{"USEC"}).ExpectsGroup, "ordinary roles keep baseline");

        var guard = EnemyIdentity.Identify("followerBully");
        var scav = EnemyIdentity.Identify("assault");
        var boss = EnemyIdentity.Identify("bossBully");
        EnemyRoleAssessment Group(double progress, int seen = 0, int allies = 0) => new(guard, default, progress, seen, allies);
        Check(Group(0).ExpectedSupport == 1 && Group(.15).ExpectedSupport == 1, "early single contact still expects escorts");
        Check(Group(.5).ExpectedSupport < 1 && Group(.85).ExpectedSupport < Group(.5).ExpectedSupport, "escort expectation falls with raid progress");
        Check(Group(1).ExpectedSupport > 0 && Group(1).Caution(.5) < Group(.15).Caution(.5), "late survivor plausible");
        Check(Group(1, 2).Caution(.5) == Group(0).Caution(.5), "recent nearby contacts outweigh late prior");
        Check(Group(0).Caution(.2) > Group(0).Caution(.8) && Group(0, 0, 2).Caution(.5) < Group(0).Caution(.5), "personality and local teammates");
        Check(Group(0).StormChance(60,.5) < Group(1).StormChance(60,.5) && Group(0).StormChance(0,.5) == 0, "squad storm weighs early escort without enabling disabled personalities");
        Check(new EnemyRoleAssessment(boss, default, 1, 0, 0).Caution(.5) > Group(1).Caution(.5), "boss remains individually dangerous");
        Check(new EnemyRoleAssessment(guard, default, null, 0, 0).ExpectedSupport == .5, "missing timer is not an opening encounter");

        var memory = new EscortSighting();
        var point = new DoorPoint(12, 4, 8);
        memory.Observe(guard, point, 10);
        memory.Observe(scav, point, 18);
        memory.Observe(boss, new DoorPoint(100, 4, 8), 5);
        Check(memory.Near(point, 25).Role == ContactRole.Guard, "a stale report or a target switch cannot erase a fresh guard sighting");
        Check(memory.Near(new DoorPoint(100, 4, 8), 25).Role == ContactRole.Unknown && memory.Near(point, 41).Role == ContactRole.Unknown, "local memory expires in space and time");
        var inArea = new EnemyRoleAssessment(scav, memory.Near(point, 25), .1, 0, 0);
        Check(inArea.Target.Role == ContactRole.Scav && inArea.Caution(.5) > 0, "local threat does not relabel a scav as a guard");

        // A visible reloading target attracts a push in the stock scoring. Only its recognition changes here.
        var c = new CometConfig();
        c.TacticalMistakes.Enabled = false;
        var s = new Situation { IsAi = true, IsSupportedBot = true, IsAuthority = true, IsActive = true, HasContact = true,
            Target = "contact", Visible = true, KnownDistance = 9, PathLength = 9, Busy = true, Weakness = .8, Looking = true,
            InCover = true, Personality = "Normal" };
        var view = new VisibleSituation { Contact = s, WeaponReady = true, CanShoot = true, PushReachable = true,
            CoverReachable = true, CoverDistance = 4, CurrentExposure = .5 };
        VisibleTactic? Pick() => new VisibleEnemyDecision(c).Evaluate(view).Decision;
        var ordinary = Pick();
        s.EnemyRole = new EnemyRoleAssessment(scav, default, 0, 0, 0);
        Check(ordinary == VisibleTactic.Push && Pick() == ordinary, "ordinary scav preserves baseline push");
        s.EnemyRole = Group(.05);
        Check(Pick() == VisibleTactic.Shoot, "hold a viable shot from cover when the first guard reloads");
        Check(new VisibleEnemyDecision(c).Evaluate(view).Candidates.Single(x => x.Decision == "Push").Eligible, "push remains eligible");
        s.Visible = false;
        s.OpenGround = true;
        s.InCover = false;
        var guarded = ZzapUtility.Hidden(s, .5, 0, false);
        s.EnemyRole = null;
        var standard = ZzapUtility.Hidden(s, .5, 0, false);
        Check(guarded[Tactic.Push] < standard[Tactic.Push] && guarded[Tactic.FallBack] > standard[Tactic.FallBack], "hidden engagement changes scores too");
        return checks;
    }
}
