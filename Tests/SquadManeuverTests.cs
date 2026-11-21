using System;
using System.Collections.Generic;
using System.Linq;
using Comet.Core;

internal static class SquadManeuverTests
{
    public static int Run()
    {
        int checks = 0;
        void C(bool ok, string why)
        {
            checks++;
            if (!ok)
                throw new Exception("S03 policy: " + why);
        }
        var c = new SquadManeuverConfig();
        c.Validate();
        C(
            c.Chance == 60 && c.Duration == 25 && c.Retry == 25 && c.MinimumAmmo == 50 && c.Enabled,
            "defaults"
        );
        foreach (
            var prop in typeof(SquadManeuverConfig)
                .GetProperties()
                .Where(x => x.PropertyType == typeof(double))
        )
        foreach (double v in new[] { double.NaN, double.PositiveInfinity, -1d, 10000d })
        {
            var config = new SquadManeuverConfig();
            prop.SetValue(config, v);
            bool rejected = false;
            try
            {
                config.Validate();
            }
            catch (ArgumentException)
            {
                rejected = true;
            }
            C(rejected, "invalid " + prop.Name);
        }
        List<ManeuverMember> Team(int count = 4) =>
            Enumerable
                .Range(0, count)
                .Select(i => new ManeuverMember
                {
                    Id = "p" + i,
                    Name = "PMC" + i,
                    Ammo = 100,
                    Score = i == 0 ? -10 : 2,
                    ContactDistanceSquared = i * i,
                })
                .ToList();
        {
            var admission = new SquadManeuverBoard();
            var members = Team(3);
            members[2].VisibleContact = true;
            C(admission.TryStart("sight", "enemy", 10, 10, 0, members, c) == null
                && admission.LastReason == SquadManeuverBoard.SomeoneSees
                && admission.LastAttempt == null, "sight veto precedes assigning roles");
            members[2].VisibleContact = false;
            var plan = admission.TryStart("sight", "enemy", 10, 10, 0, members, c);
            C(plan != null, "losing sight permits the same opportunity immediately");
            members[2].VisibleContact = true;
            C(ReferenceEquals(plan, admission.TryStart("sight", "enemy", 11, 10, 0, members, c)),
                "regaining sight preserves the existing plan");
            C(ReferenceEquals(plan, admission.TryStart("sight", "enemy", 21, 10, 0, members, c)),
                "an older personal timestamp still retrieves an active plan");
            C(admission.TryStart("sight", "other", 21, 10, 0, members, c) == null,
                "a different enemy cannot inherit the active plan");
        }
        {
            var jamTeam = Team();
            jamTeam[1].MovementBlock = "movement-awaiting-real-progress";
            var selected = new SquadManeuverBoard().TryStart(
                "jam",
                "e",
                10,
                10,
                0,
                jamTeam,
                c
            )!;
            C(
                selected.Member("p1")!.Role == "support" && selected.Member("p2")!.Role == "flank",
                "an immobile candidate can hold while a mobile teammate takes the flank"
            );
            C(selected.Members.Count(x => x.Role == "flank") == 1, "one mobile teammate flanks");
            jamTeam = Team(2);
            jamTeam[1].MovementBlock = "movement-awaiting-real-progress";
            var blocked = new SquadManeuverBoard();
            C(
                blocked.TryStart("jam2", "e", 10, 10, 0, jamTeam, c) == null,
                "no pretend maneuver when only flanker is immobilized"
            );
        }
        var board = new SquadManeuverBoard();
        var p = board.TryStart("a", "e", 10, 10, .599, Team(), c)!;
        C(p != null && p.Members.Count(x => x.Role == "flank") == 1
            && p.Members.Count(x => x.Role == "support") == 2 && p.Member("p3")!.Role == "rear",
            "four members field one flanker, two shooters and a rear guard");
        C(p.Support == "p0", "prudent support preserves aggressive movers");
        C(!p.SupportReady(10) && !p.MaySuppress, "assignment is not execution");
        var support = p.Member("p0")!;
        support.Confirmed = support.Applied = true;
        support.LastSeen = 10;
        support.Status = "holding";
        foreach (var m in p.Members.Skip(1))
        {
            m.Status = "planning";
            m.LastSeen = 10;
        }
        C(
            p.Tick(10.5, c) && p.SupportReady(10.5) && !p.MaySuppress,
            "support confirmed but no observed movement"
        );
        var mover = p.Member("p1")!;
        mover.Confirmed = mover.Applied = true;
        mover.Status = "moving";
        mover.LastSeen = 10.5;
        C(p.Tick(10.6, c) && p.MaySuppress, "moving plus confirmed support");
        mover.Status = "holding";
        C(!p.MaySuppress, "arrival stops support fire");
        p.Drop("p0", "reloading");
        C(p.Active && !p.MaySuppress, "one shooter leaving preserves the plan");
        p.Drop("p1", "route-failed");
        C(!p.Active, "loss of the only flanker ends the plan");
        board.Finish(p, 11, c, "ended");
        C(board.TryStart("a", "e", 35, 35, 0, Team(), c) == null, "retry timer");
        C(
            board.TryStart("a", "e", 36, 30, 0, Team(), c) != null,
            "same known contact can be retried after cooldown"
        );
        C(board.TryStart("a", "e", 36, 36, 0, Team(), c) != null, "new evidence and cooldown");
        board.Clear();
        C(board.Count == 0, "raid cleanup");
        C(board.TryStart("a", "e", 10, 10, .6, Team(), c) == null, "exact roll boundary");
        C(
            board.TryStart("a", "e", 10.1, 10.1, 0, Team(), c) == null,
            "failed plan roll not retried each frame"
        );
        board.Clear();
        foreach (var why in new[] { "timeout", "clock", "disabled", "all-flankers" })
        {
            board.Clear();
            p = board.TryStart("a", "e", 10, 10, 0, Team(2), c)!;
            switch (why)
            {
                case "timeout":
                    p.Tick(35, c);
                    break;
                case "callback":
                    p.Member("p0")!.Confirmed = true;
                    p.Tick(11, c);
                    break;
                case "confirm":
                    p.Tick(13, c);
                    break;
                case "clock":
                    p.Tick(9, c);
                    break;
                case "disabled":
                    var off = new SquadManeuverConfig { Enabled = false };
                    p.Tick(10, off);
                    break;
                case "all-flankers":
                    p.Drop("p1", "route-failed");
                    break;
            }
            C(!p.Active, "end " + why);
        }
        board.Clear();
        var team = Team();
        team[3].Score = -10;
        p = board.TryStart("a", "e", 10, 10, 0, team, c)!;
        C(
            p.Members.Count(x => x.Role == "rear") == 1 && p.Member(p.Support)!.Role == "support",
            "farthest teammate protects the rear without another known enemy"
        );
        board.Clear();
        p = board.TryStart("a", "e", 10, 10, 0, Team(), c)!;
        C(p.Member("p1")!.Role == "flank", "equal flank scores keep SAIN squad order");
        foreach (double rounds in new[] { 32.99, 33, 34 })
        {
            var trio = Team(3);
            trio[0].Ammo = rounds;
            trio[2].Ammo = 33;
            var three = new SquadManeuverBoard().TryStart("trio", "e", 10, 10, 0, trio, c)!;
            C(three.Member("p2")!.Role == (rounds < 33 ? "support" : "rear")
                && three.Member("p0")!.Role == (rounds < 33 ? "unassigned" : "support"),
                "exact support threshold and reassignment: " + rounds);
        }
        var dry = Team(3);
        dry[0].Ammo = dry[2].Ammo = 32;
        var noFire = new SquadManeuverBoard();
        C(noFire.TryStart("dry", "e", 10, 10, 0, dry, c) == null
            && noFire.LastReason == "maneuver-no-suppressor", "no plan without covering fire");
        dry[0].Ammo = 33;
        C(noFire.TryStart("dry", "e", 19.99, 10, 0, dry, c) == null
            && noFire.TryStart("dry", "e", 20, 10, 0, dry, c) != null, "role failure retries after ten seconds");
        var noRear = new SquadManeuverConfig { RearWatch = false };
        var shooters = new SquadManeuverBoard().TryStart("no-rear", "e", 10, 10, 0, Team(), noRear)!;
        C(shooters.Members.Count(x => x.Role == "support") == 3
            && shooters.Members.Count(x => x.Role == "flank") == 1, "existing rear-watch preference is respected");
        var secondShooter = shooters.Member("p2")!;
        secondShooter.Confirmed = secondShooter.Applied = true;
        C(shooters.SupportReady(10), "support readiness includes every assigned shooter");
        foreach (var name in new[] { "Timmy", "Rat", "SnappingTurtle", "Coward" })
            C(SquadManeuverBoard.Score(name, true, 100, c) < 0, "cautious stays support " + name);
        C(
            SquadManeuverBoard.Score("Chad", true, 100, c)
                > SquadManeuverBoard.Score("Normal", true, 100, c),
            "aggressive priority"
        );
        C(
            p.Trace("p1", 10, c).Members.Length == 4 && p.Trace("p1", 10, c).Shots == 0,
            "detached complete trace"
        );

        // A new support plan does not reset the suppressor's source burst pause.
        var fire = new SuppressionDisciplinePolicy();
        var limits = new SuppressionDisciplineConfig();
        var frame = new SuppressionDisciplineFrame
        {
            Now = 10,
            Loaded = 30,
            Capacity = 30,
            Target = "e",
            Identity = "e:1",
            SupportPlan = "one",
        };
        fire.NativeCall(frame, limits, true, default, .5);
        frame.Now = 10.1;
        frame.Loaded = 25;
        fire.NativeCall(frame, limits, true, default, .5);
        frame.SupportPlan = "two";
        C(fire.NativePaused(11), "plan changes cannot bypass source burst pause");
        C(!fire.NativePaused(13.2), "source pause expires without new-contact ledger");
        Console.WriteLine($"{checks} S03 policy/config checks passed");
        return checks;
    }
}
