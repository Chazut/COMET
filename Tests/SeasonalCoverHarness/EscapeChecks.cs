using System.Reflection.Emit;
using HarmonyLib;
using SAIN.Components;
using SAIN.SAINComponent.Classes.Mover;
using SAIN.SAINComponent.SubComponents.CoverFinder;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

public sealed partial class Plugin
{
    private static void TestExposedCoverEscape()
    {
        DamageInfo bullet = new() { DidBodyDamage = 8, Direction = new(-1,0,0), Player = new() };
        void Tick() { Time.frameCount++; Time.time += .1f; }
        void Fallback(DogFight mover) => MoveAfterCoverSearch(mover, true, mover.Bot.GoalEnemy!);

        (Plugin p, BotComponent b, DogFight mover, CoverPoint wall) Release()
        {
            var (p, b, _) = Setup(ESeason.Autumn);
            var wall = Point(false);
            wall.Spotted = true;
            b.Cover.CoverInUse = wall;
            b.EnemyController.Enemies["shooter"] = b.GoalEnemy!;
            Physics.Occludes = (_, _) => false;
            CoverExposureHit(b.Cover, bullet);
            Check(b.Cover.CoverInUse == null, "initial hit releases SAIN's exposed point");
            return (p, b, new DogFight { Bot = b }, wall);
        }

        {
            var (_, b, mover, wall) = Release();
            CoverExposureHit(b.Cover, bullet);
            Tick();
            var absorbed = bullet; absorbed.DidBodyDamage = 0;
            var bleed = bullet; bleed.DelayedDamage = true;
            var fall = bullet; fall.DamageType = EDamageType.Fall;
            foreach (var ignored in new[] { absorbed, bleed, fall })
                CoverExposureHit(b.Cover, ignored);
            Fallback(mover);
            Check(mover.Calls == 1 && b.Mover.Probes.Count == 0, "one hit, same-frame pellets and non-bullet damage keep native behavior");
            wall.Spotted = false;
            b.Cover.CoverInUse = wall;
            bool allowed = true;
            Check(CoverExposureMove(b.Mover, wall, ref allowed), "safe angle stays available before repeat damage");
            CoverExposureHit(b.Cover, bullet);
            CoverExposureRetained(b.Cover);
            Check(b.Cover.CoverInUse == null && !CoverExposureMove(b.Mover, wall, ref allowed) && !allowed,
                "a second injury dislodges a bot that reacquired the same point after the spotted flag expired");
            var near = Point(false); near.Position = new(.3f,0,0);
            allowed = true;
            Check(CoverExposureMove(b.Mover, near, ref allowed), "different cover even next to the bad spot remains eligible");
        }

        {
            var (_, b, mover, _) = Release();
            Tick(); CoverExposureHit(b.Cover, bullet);
            b.GoalEnemy!.LastKnownPosition = new(90,0,8);
            Physics.Occludes = (end, threat) => end.z > 1 && threat == new Vector3(10,1,0);
            Fallback(mover); Fallback(mover);
            Check(b.Mover.Probes.Count == 1 && b.Mover.Runs.Count == 0, "one probe per frame, remembers the first open route");
            Tick(); Fallback(mover);
            Check(b.Mover.Runs.Count == 1 && b.Mover.ActivePath!.Destination.z > 1, "prefers the reachable point hidden from the remembered shooter");
            int native = mover.Calls;
            for (int i = 0; i < 8; i++) { Tick(); CoverExposureHit(b.Cover, bullet); Fallback(mover); }
            Check(b.Mover.Runs.Count == 1 && b.Mover.Probes.Count == 2 && mover.Calls == native,
                "continued hits stay on one accepted route");
            var alternate = Point(false);
            b.Cover.CoverPoint_MovingTo = alternate;
            CoverExposureRetained(b.Cover);
            Check(b.Cover.CoverPoint_MovingTo == alternate, "new native route preserved");
            b.Cover.CoverPoint_MovingTo = null;
            Fallback(mover);
            Check(mover.Calls == native + 1 && b.Mover.Runs.Count == 1, "native cover superseded the escape");
        }

        foreach (string interrupt in new[] { "different-cover", "walked-away", "contact", "expired", "ghost", "replica", "external" })
        {
            var (p, b, mover, _) = Release();
            Tick(); CoverExposureHit(b.Cover, bullet);
            switch (interrupt)
            {
                case "different-cover": b.Cover.CoverInUse = Point(false); break;
                case "walked-away": b.Position = new(3,0,0); break;
                case "contact": b.GoalEnemy = new(); break;
                case "expired": Time.time += 7; break;
                case "ghost": p.IsGhost = true; break;
                case "replica": p.Authority = false; break;
                case "external": b.SAINLayersActive = false; break;
            }
            Fallback(mover);
            Check(b.Mover.Probes.Count == 0 && mover.Calls == 1, interrupt + " yields to native movement");
        }

        foreach (string kind in new[] { "open", "short", "invalid", "refused", "unknown" })
        {
            var (_, b, mover, _) = Release();
            if (kind == "unknown") b.EnemyController.Enemies.Clear();
            Physics.Occludes = (_, _) => kind == "unknown" ? throw new Exception("queried unobserved shooter") : false;
            b.Mover.Accept = kind != "refused";
            if (kind == "invalid") b.Mover.Route = v => new() { status = NavMeshPathStatus.PathPartial, corners = [default,v] };
            if (kind == "short") b.Mover.Route = v => v.magnitude > 6 ? null : new() { corners = [default,v] };
            Tick(); CoverExposureHit(b.Cover, bullet);
            for (int i = 0; i < 9; i++) { Tick(); Fallback(mover); }
            Check(b.Mover.Probes.Count == (kind is "invalid" or "short" ? 6 : 3), kind + " bounded path budget");
            Check(b.Mover.Runs.Count == (kind == "invalid" ? 0 : 1), kind + " only complete routes are issued once");
            if (kind == "invalid" || kind == "refused")
            {
                Check(mover.Calls == 9, kind + " keeps the native fallback after failure");
                Tick();
                CoverExposureHit(b.Cover, bullet);
                b.Mover.Route = v => new() { corners = [default,v] };
                b.Mover.Accept = true;
                Physics.Occludes = (_, _) => true;
                Fallback(mover);
                Check(b.Mover.ActivePath?.Moving == true, kind + " can retry after fresh damage without reacquiring the point");
            }
            else
            {
                int before = mover.Calls;
                b.Position = b.Mover.ActivePath!.Destination;
                Fallback(mover);
                Check(mover.Calls == before + 1 && b.Mover.Runs.Count == 1, "arrival returns to native movement");
            }
        }

        {
            var (p, a, first, _) = Release();
            var b = new BotComponent();
            var second = new DogFight { Bot = b };
            var wall = Point(false);
            wall.Spotted = true;
            b.Cover.CoverInUse = wall;
            CoverExposureHit(b.Cover, bullet);
            Tick();
            CoverExposureHit(a.Cover, bullet);
            CoverExposureHit(b.Cover, bullet);
            Fallback(first);
            Fallback(second);
            Check(a.Mover.Probes.Count + b.Mover.Probes.Count == 1, "the frame budget is shared across both bots");
            Tick(); Fallback(second);
            Check(b.Mover.Probes.Count == 1, "another bot can probe on the next frame");
            var other = Point(false);
            b.Cover.CoverPoint_MovingTo = other;
            CoverExposureRetained(b.Cover);
            Check(p.CurrentEscape(b) == null && b.Mover.Runs.Count == 0, "native cover arriving mid-search wins before any escape sprint");
        }
        {
            var (_, b, mover, _) = Release();
            Physics.Occludes = (_, _) => true;
            Tick(); CoverExposureHit(b.Cover, bullet); Fallback(mover);
            Check(b.Mover.Runs.Count == 1, "reachable screened point issued");
            b.Mover.ActivePath = new() { Moving = true, Destination = new Vector3(22,0,12) };
            Time.time += .5f;
            Fallback(mover);
            Check(mover.Calls == 1 && b.Mover.Runs.Count == 1, "a route replaced by SAIN is never reissued");
        }

        var target = AccessTools.Method(typeof(DogFight), nameof(DogFight.DogFightMove));
        var call = new CodeInstruction(OpCodes.Callvirt, target);
        var rewritten = InsertExposedCoverEscape([call]).Single();
        Check(rewritten == call && rewritten.opcode == OpCodes.Call
            && Equals(rewritten.operand, AccessTools.Method(typeof(Plugin), nameof(MoveAfterCoverSearch))),
            "only the native no-cover call is replaced in place");
    }
}
