using SAIN.Components;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;

namespace Comet.Client;

public sealed partial class Plugin
{
    private static void TestExposure()
    {
        foreach (var season in Enum.GetValues<ESeason>())
        {
            var (p, b, _) = Setup(season);
            var bush = Point(true);
            var wall = Point(false);
            b.Cover.CoverInUse = bush;
            Check(!NativeCoverProtects(b), "Foliage provides no medical protection in " + season);
            b.Cover.CoverInUse = wall;
            Check(NativeCoverProtects(b), "Arrived solid cover retains medical bonus");
            wall.BotInThisCover = false;
            Check(!NativeCoverProtects(b), "A destination still in transit is not protection");
            wall.BotInThisCover = true;
            wall.Spotted = true;
            Check(!NativeCoverProtects(b), "Native spotted cover is not protection");
            wall.Spotted = false;
            wall.CoverData.IsBad = true;
            Check(!NativeCoverProtects(b), "Native bad cover is not protection");
            wall.CoverData.IsBad = false;
            CoverExposureHit(b.Cover);
            Check(b.Cover.CoverInUse == wall, "Below native damage threshold: cover retained");
            Check(p.states[b].CoverExposure.Hits == 1, "Below-threshold hit still counted");
            wall.Spotted = true;
            b.Cover.CoverPoint_MovingTo = bush;
            b.Cover.CoverPoints.Add(wall);
            b.Cover.CoverPoints.Add(bush);
            CoverExposureHit(b.Cover);
            Check(
                b.Cover.CoverInUse == null && b.Cover.CoverPoint_MovingTo == bush,
                "Release exposed cover without dropping unrelated route"
            );
            Check(
                wall.Spotted && !wall.CoverData.IsBad && b.Cover.CoverPoints.Contains(wall),
                "Native state and candidate cache preserved"
            );
            var mover = new SAINMoverClass { Bot = b };
            bool accepted = true;
            Check(
                !CoverExposureMove(mover, wall, ref accepted) && !accepted,
                "Selection refuses exposed candidate so native loop can try next"
            );
            accepted = true;
            Check(
                CoverExposureMove(mover, bush, ref accepted) && accepted,
                "Exposure correction preserves unspotted camouflage"
            );
            wall.Spotted = false; // Native two-second expiry owns this state, not COMET.
            Check(
                CoverExposureMove(mover, wall, ref accepted),
                "Expired spotted flag eligible again, no COMET cooldown"
            );
            wall.Spotted = true;
            b.Cover.CoverPoint_MovingTo = wall;
            CoverExposureRetained(b.Cover);
            Check(
                b.Cover.CoverPoint_MovingTo == null,
                "Cached/custom exposed destination released before arrival"
            );
            b.Cover.CoverInUse = wall;
            b.Cover.CoverPoint_MovingTo = wall;
            int released = p.states[b].CoverExposure.Released;
            CoverExposureRetained(b.Cover);
            Check(
                b.Cover.CoverInUse == null
                    && b.Cover.CoverPoint_MovingTo == null
                    && p.states[b].CoverExposure.Released == released + 1,
                "Same point in both slots released once"
            );
            Check(Physics.Calls == 0, "Exposure and medical classification add no physics queries");
        }
        foreach (
            string outside in new[]
            {
                "disabled",
                "replica",
                "scav-or-boss",
                "ghost",
                "dead",
                "inactive",
                "ending",
                "external-layer",
                "no-enemy",
                "missing-plugin",
            }
        )
        {
            var (p, b, _) = Setup(ESeason.Summer);
            switch (outside)
            {
                case "disabled":
                    p.config!.Enabled = false;
                    break;
                case "replica":
                    p.Authority = false;
                    break;
                case "scav-or-boss":
                    b.Supported = false;
                    break;
                case "ghost":
                    p.IsGhost = true;
                    break;
                case "dead":
                    b.IsDead = true;
                    break;
                case "inactive":
                    b.BotActive = false;
                    break;
                case "ending":
                    b.GameEnding = true;
                    break;
                case "external-layer":
                    b.SAINLayersActive = false;
                    break;
                case "no-enemy":
                    b.GoalEnemy = null;
                    break;
                case "missing-plugin":
                    Instance = null;
                    break;
            }
            var point = Point(true);
            point.Spotted = true;
            b.Cover.CoverInUse = b.Cover.CoverPoint_MovingTo = point;
            CoverExposureHit(b.Cover);
            CoverExposureRetained(b.Cover);
            bool accepted = true;
            Check(
                CoverExposureMove(new() { Bot = b }, point, ref accepted) && accepted,
                outside + ": native mover preserved"
            );
            Check(
                b.Cover.CoverInUse == point && b.Cover.CoverPoint_MovingTo == point,
                outside + ": native cover preserved"
            );
        }
        {
            var (p, b, _) = Setup(ESeason.Summer);
            var point = Point(false);
            point.Spotted = true;
            Diagnostics.Fail = true;
            b.Cover.CoverInUse = point;
            CoverExposureHit(b.Cover);
            Check(b.Cover.CoverInUse == null, "Failed telemetry cannot veto release");
            Diagnostics.Fail = false;
            Time.time += 5;
            int sent = Api.Decisions.Count;
            for (int i = 0; i < 1000; i++)
            {
                bool result = true;
                CoverExposureMove(new() { Bot = b }, point, ref result);
            }
            Check(
                Api.Decisions.Count == sent + 1 && p.states[b].CoverExposure.Refused == 1000,
                "Refusals aggregated, one diagnostic per five seconds"
            );
            p.diagnostics = null;
            Time.time += 5;
            bool accepted = true;
            CoverExposureMove(new() { Bot = b }, point, ref accepted);
            Check(Api.Decisions.Count == sent + 2, "RR receives event without disk writer");
        }
    }
}
