using Comet.Client;
using EFT;
using SAIN;
using SAIN.Components;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
using UnityEngine.AI;

internal static class SquadManeuverHarness
{
    public static void Run()
    {
        int checks = 0;
        var events = new List<string>();
        void C(bool ok, string why)
        {
            checks++;
            if (!ok)
                throw new Exception("S03 adapter: " + why);
        }
        void Step(float dt = .02f)
        {
            Time.time += dt;
            Time.realtimeSinceStartup = Time.time;
            Time.frameCount++;
        }
        (Plugin, BotComponent[]) Setup(int count = 4)
        {
            Time.time = Time.realtimeSinceStartup = 10;
            Time.frameCount = 0;
            Physics.Blocked = false;
            Physics.Cast = null;
            NavMesh.Valid = true;
            NavMesh.Sample = null;
            NavMesh.Route = null;
            SAINEnableClass.Bots.Clear();
            Api.Decisions.Labels.Clear();
            var p = new Plugin();
            p.config!.Crossfire.Participation = 100;
            p.config.SquadManeuver.Chance = 100;
            p.config.SecondaryEntrance.Enabled = false;
            var bots = Enumerable
                .Range(0, count)
                .Select(i => new BotComponent
                {
                    Position =
                        i == 0 ? new(0, 0, 0)
                        : i == 1 ? new(-3, 0, 0)
                        : i == 2 ? new(3, 0, 0)
                        : new(7, 0, -3),
                })
                .ToArray();
            bots[0].Info.Personality = Personality.Rat;
            var members = bots.ToDictionary(b => b.ProfileId);
            foreach (var b in bots)
            {
                b.Squad.Members = members;
                b.GoalEnemy!.KnownPlaces.LastKnownPosition = new(0, 0, 30);
                b.EnemyController.KnownEnemies.Add(b.GoalEnemy);
                SAINEnableClass.Bots[b.ProfileId] = b;
            }
            return (p, bots);
        }
        CrossfireAction Action(Plugin p, BotComponent b)
        {
            var l = new CrossfireLayer(b.BotOwner, 21);
            C(l.IsActive(), "native layer active");
            var a = new CrossfireAction(b.BotOwner);
            a.Start();
            a.OnSteeringTicked();
            return a;
        }
        (Plugin p, BotComponent[] bots, CrossfireAction[] actions) Start(
            int count = 4,
            bool queued = false
        )
        {
            var (p, bots) = Setup(count);
            foreach (var b in bots)
            {
                b.Decision.CurrentCombatDecision = SAIN.Preset.Shared.Enums.ECombatDecision.Search;
                b.Mover.ActivePath = new BotPathDataManual
                {
                    TimeStarted = Time.time,
                    Destination = new(0, 0, 20),
                    OnStop = () => b.Mover.ActivePath = null,
                };
                if (queued)
                {
                    var prepared = new BotPathDataManual
                    {
                        TimeStarted = -1,
                        Destination = new(1, 0, 20),
                        OnStop = () => b.Mover.ActivePath = null,
                    };
                    b.Mover.QueuedPath = prepared;
                    ((BotPathDataManual)b.Mover.ActivePath).OnStop = () =>
                    {
                        b.Mover.ActivePath = prepared;
                        b.Mover.QueuedPath = null;
                        prepared.TimeStarted = Time.time;
                    };
                }
                p.Observe(b);
            }
            C(p.states[bots[0]].Crossfire.Maneuver != null, "shared plan created");
            var actions = new CrossfireAction[count];
            actions[0] = Action(p, bots[0]);
            for (int i = 1; i < count; i++)
            {
                var r = p.states[bots[i]].Crossfire;
                r.Candidates.Clear();
                r.Candidates.Add(
                    i == 1 ? new(-20, 0, 20)
                    : i == 2 ? new(20, 0, 20)
                    : new(12, 0, 30)
                );
                r.Candidate = 0;
                r.Route = null;
                r.Samples.Clear();
            }
            for (int n = 0; n < 180; n++)
            {
                Step();
                actions[0].Update(new());
                actions[0].OnSteeringTicked();
                for (int i = 1; i < count; i++)
                {
                    p.Sample(bots[i]);
                    if (
                        p.states[bots[i]].Crossfire.Policy.Trace?.State == "ready"
                        && actions[i] == null
                    )
                        actions[i] = Action(p, bots[i]);
                    if (actions[i] != null)
                    {
                        actions[i].Update(new());
                        actions[i].OnSteeringTicked();
                    }
                }
                if (actions.All(x => x != null))
                    break;
            }
            for (int i = 1; i < count; i++)
                C(
                    p.states[bots[i]].Crossfire.Policy.Trace?.State == "moving",
                    "flanker actual route "
                        + i
                        + ": "
                        + p.states[bots[i]].Crossfire.Policy.Trace?.Reason
                        + " / "
                        + string.Join(",", p.states[bots[i]].Crossfire.Rejects)
                );
            return (p, bots, actions);
        }
        {
            var (p, bots, actions) = Start();
            var b = bots[1];
            var r = p.states[b].Crossfire;
            var plan = r.Maneuver!;
            double until = plan.Until;
            b.Position = r.Goal;
            b.GoalEnemy!.IsVisible = true;
            Step();
            actions[1].Update(new());
            C(
                !r.Policy.Active && r.Policy.Trace!.Outcome == "engaged-from-new-angle",
                "coordinated flanker hands actual acquired angle to combat"
            );
            C(
                plan.Active
                    && plan.State == "converging"
                    && plan.Member(b.ProfileId)!.Status == "engaging",
                "success is retained on shared plan"
            );
            C(
                !plan.MaySuppress && plan.Until == until,
                "success stops blind suppression without shortening other flank routes"
            );
            Step();
            actions[0].Update(new());
            actions[0].OnSteeringTicked();
            actions[2].Update(new());
            C(
                p.states[bots[2]].Crossfire.Policy.Active,
                "another flanker continues after first engages"
            );
            events.AddRange(p.diagnostics!.Lines);
            p.EndAll();
        }
        foreach (float pause in new[] { .15f, .45f })
        {
            var (p, bots, actions) = Start();
            var b = bots[1];
            var r = p.states[b].Crossfire;
            var plan = r.Maneuver;
            var path = b.Mover.ActivePath;
            var walks = b.Mover.Walks;
            b.Player.MovementContext.IsGrounded = false;
            Step();
            actions[1].Update(new());
            Step(pause);
            actions[0].Update(new());
            actions[0].OnSteeringTicked();
            actions[1].Update(new());
            C(
                r.Policy.Active && ReferenceEquals(r.Maneuver, plan) && p.Claims == 4,
                "coordinated flank keeps shared plan and reservations during short ground loss"
            );
            b.Player.MovementContext.IsGrounded = true;
            Step();
            actions[1].Update(new());
            C(
                r.Policy.Active
                    && ReferenceEquals(path, b.Mover.ActivePath)
                    && walks == b.Mover.Walks,
                "coordinated flank resumes the same native route"
            );
            b.Position = r.Goal;
            Step();
            actions[1].Update(new());
            C(
                r.Policy.Trace!.State == "holding",
                "coordinated flank reaches its angle after the bounce"
            );
            p.EndAll();
        }
        {
            var (p, bots, actions) = Start();
            C(p.Claims == 4, "three distinct routes and one support reservation");
            C(
                p.states[bots[0]].Crossfire.Maneuver!.Members.Count(x => x.Confirmed) == 4,
                "all roles confirmed by native callbacks"
            );
            C(
                bots[0].ManualShoot.Requests > 0,
                "personal eligible support requests native W07 fire"
            );
            C(
                p.states[bots[0]].Crossfire.Policy.Trace!.Maneuver!.Shots == 0,
                "request is not an actual shot"
            );
            C(
                p.states[bots[0]].Crossfire.Policy.Trace!.Reason == "maneuver-support-established",
                "support is not reported as flanker arrival"
            );
            var r = p.states[bots[1]].Crossfire;
            bots[1].Position = r.Goal;
            Step();
            actions[1].Update(new());
            actions[0].Update(new());
            C(!r.Maneuver!.MaySuppress, "arrival stops suppression");
            p.EndAll();
            C(p.Claims == 0 && Api.Decisions.Labels.Count == 0, "cleanup");
            events.AddRange(p.diagnostics!.Lines);
        }
        {
            var (p, bots, actions) = Start(2, true);
            C(
                bots.All(b => p.states[b].Crossfire.Policy.Trace!.Handoff!.ReleasedPaths == 2),
                "raid399: both observed native paths released"
            );
            C(
                p.states[bots[0]].Crossfire.Maneuver!.Members.All(m => m.Confirmed),
                "raid399: queued-path support and flanker really confirmed"
            );
            C(
                p.states[bots[1]].Crossfire.Policy.Trace!.State == "moving",
                "raid399: flanker actually moves after support callback"
            );
            events.AddRange(p.diagnostics!.Lines);
            p.EndAll();
        }
        {
            var (p, bots, actions) = Start(2);
            var support = bots[0];
            var mover = bots[1];
            support.GoalEnemy!.IsVisible = true;
            actions[0].Update(new());
            actions[0].OnSteeringTicked();
            actions[1].Update(new());
            C(
                p.states[mover].Crossfire.Policy.Active && actions[0].NativeSteering > 0,
                "visible anchor preserves native aim/fire and does not cancel flanker"
            );
            C(
                !p.states[support].Crossfire.ManeuverFire,
                "visible support does not restart blind suppression"
            );
            var shot = typeof(Plugin).GetMethod(
                "ManeuverShot",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
            )!;
            int eventCount = p.diagnostics!.Lines.Count;
            for (int i = 0; i < 30; i++)
                shot.Invoke(p, new object[] { p.states[support] });
            C(
                p.states[support].Crossfire.ManeuverRole!.Shots == 30,
                "all native visible support shots counted"
            );
            C(
                p.diagnostics.Lines.Count - eventCount <= 1,
                "shot burst coalesces expensive member snapshots without losing counters"
            );
            support.GoalEnemy.IsVisible = false;
            support.GoalEnemy.TimeSinceLastKnownUpdated = 9;
            mover.GoalEnemy!.TimeSinceLastKnownUpdated = 9;
            Step();
            actions[0].Update(new());
            actions[0].OnSteeringTicked();
            actions[1].Update(new());
            C(
                p.states[mover].Crossfire.Policy.Active,
                "support retains a confirmed progressing flank despite old contact"
            );
            mover.Position = p.states[mover].Crossfire.Goal;
            Step();
            actions[1].Update(new());
            C(
                p.states[mover].Crossfire.Policy.Trace!.State == "holding",
                "coordinated remembered route reaches goal"
            );
            Step(.11f);
            actions[0].Update(new());
            C(
                p.states[support].Crossfire.Policy.Trace!.Maneuver!.Shots == 30,
                "next support snapshot retains entire shot count"
            );
            events.AddRange(p.diagnostics!.Lines);
            p.EndAll();
        }
        {
            var (p, bots) = Setup(2);
            bots[0].GoalEnemy!.IsVisible = true;
            foreach (var b in bots)
                p.Observe(b);
            C(
                p.states[bots[1]].Crossfire.Maneuver == null
                    && p.states[bots[1]].Crossfire.Policy.Active,
                "a teammate who already sees the target enables ordinary crossfire"
            );
            p.EndAll();
        }
        foreach (
            var fault in new[]
            {
                "reload",
                "heal",
                "ghost",
                "remote",
                "distance",
                "foreign-route",
                "callbacks",
            }
        )
        {
            var (p, bots, actions) = Start(2);
            var support = bots[0];
            var mover = bots[1];
            var path = (BotPathDataManual)mover.Mover.ActivePath!;
            switch (fault)
            {
                case "reload":
                    support.BotOwner.WeaponManager.Reload.Reloading = true;
                    break;
                case "heal":
                    support.BotOwner.Medecine.Using = true;
                    break;
                case "ghost":
                    p.Ghosts.Add(support.ProfileId);
                    break;
                case "remote":
                    p.Authority = false;
                    break;
                case "visible":
                    support.GoalEnemy!.IsVisible = true;
                    break;
                case "contact":
                    support.GoalEnemy!.TimeSinceLastKnownUpdated = 9;
                    break;
                case "distance":
                    support.Position = new(90, 0, 0);
                    break;
                case "foreign-route":
                    support.Mover.ActivePath = new BotPathDataManual();
                    actions[0].Update(new());
                    break;
                case "callbacks":
                    Step(1);
                    break;
            }
            actions[1].Update(new());
            C(
                !p.states[mover].Crossfire.Policy.Active && path.Stops == 1,
                "support interruption releases owned path " + fault
            );
            events.AddRange(p.diagnostics!.Lines);
        }
        {
            var (p, bots) = Setup(2);
            foreach (var b in bots)
                p.Observe(b);
            Step(3.1f);
            p.Sample(bots[1]);
            C(
                !p.states[bots[1]].Crossfire.Policy.Active && bots[1].Mover.Walks == 0,
                "unconfirmed support never moves flanker"
            );
            events.AddRange(p.diagnostics!.Lines);
        }
        {
            var (p, bots, actions) = Start(2);
            var b = bots[1];
            var old = (BotPathDataManual)b.Mover.ActivePath!;
            b.Mover.ActivePath = new BotPathDataManual { Destination = new(90, 0, 0) };
            actions[1].Update(new());
            C(old.Stops == 0 && !p.states[b].Crossfire.Policy.Active, "foreign movement preserved");
            events.AddRange(p.diagnostics!.Lines);
        }
        foreach (
            var mode in new[]
            {
                "disabled",
                "scav",
                "human",
                "low-ammo",
                "stale",
                "too-close",
                "visible",
                "zero-roll",
            }
        )
        {
            var (p, bots) = Setup(2);
            switch (mode)
            {
                case "disabled":
                    p.config!.SquadManeuver.Enabled = false;
                    break;
                case "scav":
                    bots[1].Pmc = false;
                    break;
                case "human":
                    bots[1].Player.IsAI = false;
                    break;
                case "low-ammo":
                    bots[1].BotOwner.WeaponManager.CurrentWeapon.Mag.Count = 1;
                    break;
                case "stale":
                    foreach (var b in bots)
                        b.GoalEnemy!.TimeSinceLastKnownUpdated = 9;
                    break;
                case "too-close":
                    foreach (var b in bots)
                        b.GoalEnemy!.KnownPlaces.LastKnownPosition = new(0, 0, 4);
                    break;
                case "visible":
                    bots[1].GoalEnemy!.IsVisible = true;
                    break;
                case "zero-roll":
                    p.config!.SquadManeuver.Chance = 0;
                    break;
            }
            foreach (var b in bots)
                p.Observe(b);
            C(p.states.Values.All(s => s.Crossfire.Maneuver == null), "no invalid plan " + mode);
            events.AddRange(p.diagnostics!.Lines);
        }

        {
            var (p, bots) = Setup(2);
            bots[0].Player.LookDirection = new(0, 0, -1);
            foreach (var b in bots)
                p.Observe(b);
            var action = Action(p, bots[0]);
            C(
                !p.states[bots[0]].Crossfire.Maneuver!.SupportReady(Time.time),
                "steering request is not confirmed facing"
            );
            Step(3.1f);
            p.Sample(bots[1]);
            C(!p.states[bots[1]].Crossfire.Policy.Active, "unfaced support times out");
            events.AddRange(p.diagnostics!.Lines);
        }
        {
            var (p, bots, actions) = Start(2);
            p.config!.SuppressionDiscipline.Enabled = false;
            int before = bots[0].ManualShoot.Requests;
            actions[0].Update(new());
            C(bots[0].ManualShoot.Requests == before, "W07 off never creates unguarded fire");
            p.EndAll();
            C(p.states.Values.All(s => !s.Crossfire.ManeuverFire), "plan fire ownership released");
            events.AddRange(p.diagnostics!.Lines);
        }
        {
            var (p, bots, actions) = Start(2);
            var b = bots[1];
            b.BotOwner.WeaponManager.CurrentWeapon.Mag.Count = 1;
            actions[1].Update(new());
            C(!p.states[b].Crossfire.Policy.Active, "flanker low ammo aborts");
            events.AddRange(p.diagnostics!.Lines);
        }
        {
            var (p, bots, actions) = Start(2);
            p.config!.SquadManeuver.StallTimeout = 1;
            for (int i = 0; i < 60; i++)
            {
                Step();
                actions[0].Update(new());
                actions[0].OnSteeringTicked();
                actions[1].Update(new());
                actions[1].OnSteeringTicked();
            }
            C(
                p.states[bots[1]].Crossfire.Policy.Trace!.Reason.Contains("stalled"),
                "configured S03 stall bound"
            );
            events.AddRange(p.diagnostics!.Lines);
        }
        {
            var (p, bots, actions) = Start();
            p.End(bots[1]);
            C(
                p.states[bots[2]].Crossfire.Maneuver!.Active,
                "one failed flanker does not end other routes"
            );
            Step();
            actions[0].Update(new());
            actions[0].OnSteeringTicked();
            actions[2].Update(new());
            C(p.states[bots[2]].Crossfire.Policy.Active, "remaining flanker continues");
            events.AddRange(p.diagnostics!.Lines);
            p.EndAll();
        }
        foreach (var mode in new[] { "custom-distance", "custom-age", "declined-preserves-s01" })
        {
            var (p, bots) = Setup(2);
            p.config!.SquadManeuver.MaxDistance = 120;
            p.config.SquadManeuver.ContactAge = 12;
            foreach (var b in bots)
            {
                if (mode == "custom-age")
                    b.GoalEnemy!.TimeSinceLastKnownUpdated = 10;
                else
                    b.GoalEnemy!.KnownPlaces.LastKnownPosition = new(0, 0, 100);
            }
            if (mode == "declined-preserves-s01")
                p.config.SquadManeuver.Chance = 0;
            foreach (var b in bots)
                p.Observe(b);
            C(
                p.states.Values.All(s =>
                    s.Crossfire.Policy.Active == (mode != "declined-preserves-s01")
                ),
                "S03 initial limits and S01 fallback: " + mode
            );
            if (mode != "declined-preserves-s01")
                C(
                    p.states.Values.All(s => s.Crossfire.Maneuver != null),
                    "custom limit belongs to S03: " + mode
                );
            events.AddRange(p.diagnostics!.Lines);
            p.EndAll();
        }

        {
            var (p, bots, actions) = Start();
            bots[3].IsDead = true;
            bots[0].Squad.Members.Remove(bots[3].ProfileId);
            Step();
            actions[0].Update(new());
            actions[0].OnSteeringTicked();
            actions[1].Update(new());
            C(
                p.states[bots[1]].Crossfire.Policy.Active,
                "death of another flanker preserves support and surviving flank"
            );
            C(
                p.states[bots[1]].Crossfire.Maneuver!.Member(bots[3].ProfileId)!.Status == "ended",
                "departed role explicitly retired"
            );
            p.EndAll();
        }
        var dest = Path.GetFullPath("artifacts/s03-adapter-events.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.WriteAllLines(dest, events);
        Console.WriteLine(
            $"{checks} S03 production-adapter checks passed; {events.Count} events exported"
        );
    }
}
