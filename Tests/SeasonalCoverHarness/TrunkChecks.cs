using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

public sealed partial class Plugin
{
    private static void TestTrunkCover()
    {
        Vector3 east = new(10, 0, 0);
        Vector3 west = new(-10, 0, 0);
        foreach (var season in Enum.GetValues<ESeason>())
        {
            var (_, bot, analyzer) = Setup(season);
            var trunk = Point(false);
            var branches = Point(true);
            Physics.Inside = _ => true;
            Physics.Obstacles = [(new(1, 1, 0), 12, false)];
            bot.GoalEnemy!.LastKnownPosition = east;
            var hit = new NavMeshHit { position = trunk.Position };
            string reason = "native";
            bool accepted = true;
            SeasonalCoverPosition(analyzer, trunk.Collider, east, hit, ref reason, ref accepted);
            Check(accepted && reason == "native", season + ": trunk shelters point at body height");
            bot.Cover.CoverPoints.AddRange([branches, trunk]);
            bot.Cover.CoverInUse = bot.Cover.CoverPoint_MovingTo = trunk;
            SeasonalCoverSelection(bot.Cover);
            SeasonalCoverRetained(bot.Cover);
            Check(
                bot.Cover.CoverPoints.Contains(trunk)
                    && bot.Cover.CoverInUse == trunk
                    && bot.Cover.CoverPoint_MovingTo == trunk,
                season + ": protective trunk survives selection and retention"
            );
            Check(NativeCoverProtects(bot), season + ": arrived trunk can protect a healing bot");
            Check(
                SeasonalCoverMove(new SAINMoverClass { Bot = bot }, trunk, ref accepted),
                season + ": native move to sheltered trunk allowed"
            );
            if (season is not (ESeason.Autumn or ESeason.AutumnLate or ESeason.Winter))
            {
                Check(
                    Physics.SolidQueries == 0 && Physics.Calls == 0,
                    season + ": native seasonal behavior"
                );
                continue;
            }
            Check(
                !bot.Cover.CoverPoints.Contains(branches),
                "Branches alone never supply a solid candidate"
            );
            int queries = Physics.SolidQueries;
            Check(
                SeasonalCoverUsable(bot, trunk) && Physics.SolidQueries == queries,
                "Same-frame move and retention share the ray"
            );
            Check(
                !BareFoliageAt(trunk.Position, east),
                "Relocation position sheltered from known threat"
            );
            Check(
                BareFoliageAt(trunk.Position, west),
                "Relocation on exposed side of same trunk refused"
            );
            bot.GoalEnemy.LastKnownPosition = west;
            SeasonalCoverRetained(bot.Cover);
            Check(
                bot.Cover.CoverInUse == null && bot.Cover.CoverPoint_MovingTo == null,
                "Changed threat direction invalidates retained protection immediately"
            );
            bot.GoalEnemy.LastKnownPosition = null;
            Check(
                !SeasonalCoverUsable(bot, trunk),
                "Unknown threat cannot establish directional protection"
            );
            accepted = false;
            SeasonalCoverPosition(analyzer, trunk.Collider, east, hit, ref reason, ref accepted);
            Check(!accepted, "Protective trunk cannot override native refusal");

            Physics.Obstacles =
            [
                (new(1, 1, 0), 26, false),
                (new(.8f, 1, 0), 31, false),
                (new(.6f, 1, 0), 12, true),
                (new(4, 1, 0), 12, false),
            ];
            Check(
                BareFoliageAt(trunk.Position, east),
                "Branches, grass, trigger and distant wall do not protect the position"
            );
            Physics.Obstacles = [(new(1.5f, 1, 0), 12, false)];
            Check(
                BareFoliageAt(trunk.Position, new(1, 0, 0)),
                "Solid obstacle beyond the threat is not cover"
            );
            Check(
                !BareFoliageAt(trunk.Position, east),
                "Nearby wall or rock qualifies the same way as a trunk"
            );
            bot.GoalEnemy.LastKnownPosition = east;
            Check(SeasonalCoverUsable(bot, trunk), "Solid cover acquired after threat change");
            Physics.Obstacles = [];
            Time.frameCount++;
            Check(
                !SeasonalCoverUsable(bot, trunk),
                "Removed or moved obstacle cannot leave a stale positive"
            );
            var grass = Point(false);
            grass.Collider.gameObject.layer = 31;
            Check(!FoliageCoverCollider(grass.Collider), "Grass layer is not Foliage");
        }
    }
}
