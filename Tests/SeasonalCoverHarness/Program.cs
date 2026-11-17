using SAIN.Components;
using SAIN.SAINComponent.Classes.Mover;
using SAIN.SAINComponent.SubComponents.CoverFinder;
using UnityEngine;
using UnityEngine.AI;

Comet.Client.Plugin.Test();

namespace Comet.Client
{
    public sealed partial class Plugin
    {
        private static int checks;
        private static void Check(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }
        private static (Plugin p, BotComponent b, CoverAnalyzer analyzer) Setup(ESeason season)
        {
            Instance = new Plugin();
            var b = new BotComponent();
            Instance.states[b] = new();
            SeasonsController.Controller = new() { Season = season };
            Physics.Inside = _ => false; Physics.Calls = 0; Physics.Fail = false;
            Diagnostics.Fail = false; Api.Decisions.Count = 0; Time.time = 10;
            return (Instance, b, new() { Bot = b });
        }
        private static CoverPoint Point(bool foliage) => new() { Collider = new() { gameObject = new() { layer = foliage ? 7 : 4 } } };
        public static void Test()
        {
            TestExposure();
            foreach (var season in Enum.GetValues<ESeason>())
            {
                var (p, b, a) = Setup(season);
                var bush = Point(true); var wall = Point(false);
                bool bareSeason = season is ESeason.Autumn or ESeason.AutumnLate or ESeason.Winter;
                bool result = true; string reason = "native"; var hit = new NavMeshHit { position = new(5, 2, 3) };
                bool run = SeasonalCoverCandidate(a, bush.Collider, ref reason, ref hit, ref result);
                Check(run != bareSeason && result != bareSeason, season + ": foliage candidate");
                Check(Physics.Calls == 0, "Foliage layer rejection has no physics query");
                Check(SeasonalCoverUsable(b, bush) != bareSeason, "Cached/custom bush respects season");
                Check(SeasonalCoverUsable(b, wall), "Solid wall retained");
                Check(!wall.CoverData.IsBad && !bush.CoverData.IsBad, "No permanent native blacklist");
                result = true; reason = "native";
                Check(SeasonalCoverCandidate(a, wall.Collider, ref reason, ref hit, ref result) && result, "Solid candidate runs native geometry");
                Physics.Inside = _ => true;
                result = true;
                SeasonalCoverPosition(a, wall.Collider, hit, ref reason, ref result);
                Check(result != bareSeason, "Solid candidate inside foliage rejected only in autumn/winter");
                int calls = Physics.Calls;
                result = false;
                SeasonalCoverPosition(a, wall.Collider, hit, ref reason, ref result);
                Check(!result && Physics.Calls == calls, "Native geometry failure unchanged without extra query");
            }
            foreach (var season in new[] { ESeason.Autumn, ESeason.AutumnLate, ESeason.Winter })
            foreach (string outside in new[] { "disabled", "replica", "scav-or-boss", "ghost", "dead", "inactive", "ending", "external-layer", "no-enemy", "unknown-season", "missing-plugin" })
            {
                var (p, b, a) = Setup(season);
                switch (outside)
                {
                    case "disabled": p.config!.Enabled = false; break;
                    case "replica": p.Authority = false; break;
                    case "scav-or-boss": b.Supported = false; break;
                    case "ghost": p.IsGhost = true; break;
                    case "dead": b.IsDead = true; break;
                    case "inactive": b.BotActive = false; break;
                    case "ending": b.GameEnding = true; break;
                    case "external-layer": b.SAINLayersActive = false; break;
                    case "no-enemy": b.GoalEnemy = null; break;
                    case "unknown-season": SeasonsController.Controller = null; break;
                    case "missing-plugin": Instance = null; break;
                }
                var bush = Point(true); string reason = "original"; bool result = true; NavMeshHit hit = default;
                Check(SeasonalCoverCandidate(a, bush.Collider, ref reason, ref hit, ref result) && result && reason == "original", outside + ": native preserved");
                b.Cover.CoverPoints.Add(bush); b.Cover.CoverInUse = bush;
                SeasonalCoverSelection(b.Cover); SeasonalCoverRetained(b.Cover);
                Check(b.Cover.CoverPoints.Count == 1 && ReferenceEquals(b.Cover.CoverInUse, bush), outside + ": retained untouched");
                Check(Physics.Calls == 0 && p.diagnostics!.Count == 0, outside + ": no scan/log work");
            }
            foreach (var season in new[] { ESeason.Autumn, ESeason.AutumnLate, ESeason.Winter })
            {
                var (p, b, _) = Setup(season);
                var bush = Point(true); var wall = Point(false);
                b.Cover.CoverPoints.AddRange(new[] { bush, wall }); b.Cover.CoverInUse = bush; b.Cover.CoverPoint_MovingTo = wall;
                SeasonalCoverSelection(b.Cover); SeasonalCoverRetained(b.Cover);
                Check(b.Cover.CoverPoints.SequenceEqual(new[] { wall }), "Native selector retains solid alternative");
                Check(b.Cover.CoverInUse == null && ReferenceEquals(b.Cover.CoverPoint_MovingTo, wall), "Release invalid hold without erasing valid movement");
                bool result = true;
                Check(!SeasonalCoverMove(new() { Bot = b }, bush, ref result) && !result, "Custom move cannot reintroduce foliage point");
                b.Cover.CoverPoint_MovingTo = bush;
                SeasonalCoverRetained(b.Cover);
                Check(b.Cover.CoverPoint_MovingTo == null, "Retained invalid destination released");
                result = false;
                Check(SeasonalCoverMove(new() { Bot = b }, wall, ref result) && !result, "Native solid move result is not forced true");
                Check(!bush.CoverData.IsBad, "Seasonal refusal is reversible");
                Check(p.diagnostics!.Count == 1 && Api.Decisions.Count == 1, "Many cover refusals have one bounded log and RR event");
                using var logged = System.Text.Json.JsonDocument.Parse(p.diagnostics.Last!);
                Check(logged.RootElement.GetProperty("seasonalCover").GetProperty("season").GetString() == season.ToString(), "Diagnostic reports actual season");
                Check(logged.RootElement.GetProperty("reason").GetString() == "seasonal-cached-foliage-cover-removed", "Shared seasonal reason remains accurate in winter");
                Time.time += 5;
                SeasonalCoverMove(new() { Bot = b }, bush, ref result);
                Check(p.diagnostics.Count == 2 && p.states[b].SeasonalCover.Rejected == 5, "Next bounded log preserves cumulative refusals");
                Diagnostics.Fail = true; Time.time += 5; result = true;
                Check(!SeasonalCoverMove(new() { Bot = b }, bush, ref result) && !result, "Observer fault cannot restore invalid cover");
            }
            {
                var (_, b, _) = Setup(ESeason.Summer);
                var wall = Point(false);
                Physics.Inside = _ => true;
                Check(SeasonalCoverUsable(b, wall) && Physics.Calls == 0, "Summer has zero occupancy queries");
                SeasonsController.Controller!.Season = ESeason.AutumnLate;
                Check(!SeasonalCoverUsable(b, wall) && Physics.Calls == 1, "Cached summer point rechecked on autumn entry");
                for (int i = 0; i < 1000; i++) Check(!SeasonalCoverUsable(b, wall), "Cached occupancy remains refused");
                Check(Physics.Calls == 1, "Repeated decisions/movement do not repeat physics queries");
                Check(Physics.LastBuffer!.All(c => c == null), "Shared overlap buffer does not retain scene colliders");
                Physics.Inside = _ => false; wall.Position = new(1, 0, 0);
                Check(SeasonalCoverUsable(b, wall) && Physics.Calls == 2, "Native moved point rechecked");
                SeasonsController.Controller.Season = ESeason.Autumn;
                Physics.Inside = _ => true;
                Check(!SeasonalCoverUsable(b, wall) && Physics.Calls == 3, "Season change invalidates occupancy cache");
                b.Cover.CoverInUse = wall;
                SeasonalCoverRetained(b.Cover);
                Check(b.Cover.CoverInUse == null, "Solid cover inside foliage released too");
                SeasonsController.Controller.Season = ESeason.Winter;
                Check(!SeasonalCoverUsable(b, wall) && Physics.Calls == 4, "Winter rechecks cached autumn position and still rejects bush occupancy");
                for (int i = 0; i < 1000; i++) Check(!SeasonalCoverUsable(b, wall), "Winter decisions reuse occupancy result");
                Check(Physics.Calls == 4, "Winter adds no repeated physics queries");
                SeasonsController.Controller.Season = ESeason.Spring;
                Check(SeasonalCoverUsable(b, wall) && Physics.Calls == 4, "Spring restores native policy without scan or permanent bad-point flag");
                Physics.Fail = true;
                try { CoverPositionInFoliage(default); throw new Exception("Expected physics fault"); }
                catch (Exception ex) when (ex.Message == "Physics test fault") { }
                Check(Physics.LastBuffer!.All(c => c == null), "Overlap buffer clears on exceptions");
            }
            Console.WriteLine($"Seasonal cover: {checks} production checks passed (scope, geometry, retained/custom paths, seasons, cache and observer faults).");
        }
    }
}
