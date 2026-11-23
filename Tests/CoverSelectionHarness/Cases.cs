using System;
using System.Collections.Generic;
using System.Linq;

namespace Comet.Client;

public static class Cases
{
    public static void Run()
    {
        int checks = 0;
        void Check(bool value, string message)
        {
            checks++;
            if (!value)
                throw new Exception(message);
        }
        var plugin = Plugin.Instance = new Plugin();
        var bot = new BotComponent();
        var cover = bot.Cover;
        cover.Bot = bot;
        var close = new CoverPoint(3);
        var far = new CoverPoint(7);
        cover.CoverPoints.AddRange(new[] { far, close });
        var state = plugin.states[bot] = new BotState();
        var progress = new PlanProgress();

        // Before contact the current native route has priority.
        cover.CoverPoint_MovingTo = close;
        Check(VisibleAdapter.SelectCover(bot) == close, "moving point without a target");
        cover.CoverInUse = far;
        Check(VisibleAdapter.SelectCover(bot) == far, "occupied point before moving point");
        bot.GoalEnemy = new Enemy { IsVisible = true, EnemyPosition = new(10, 1, 2) };
        Check(
            VisibleAdapter.SelectCover(bot, progress) == close,
            "nearest point screening this enemy"
        );
        Check(
            Physics.LastEye == new Vector3(10, 2.5f, 2),
            "visible enemy position is the observation origin"
        );
        Physics.Exposed.Add(3);
        Check(VisibleAdapter.SelectCover(bot) == far, "exposed candidate skipped");
        Physics.Exposed.Clear();
        close.Usable = false;
        Check(
            VisibleAdapter.SelectCover(bot) == far,
            "cover filter refusal does not hide the next usable point"
        );
        bot.GoalEnemy.IsVisible = false;
        bot.GoalEnemy.KnownPlaces.LastKnownPosition = new(17, 0, 0);
        VisibleAdapter.SelectCover(bot);
        Check(
            Physics.LastEye == new Vector3(17, 1.5f, 0),
            "hidden target uses last known position"
        );
        bot.GoalEnemy.KnownPlaces.LastKnownPosition = null;
        VisibleAdapter.SelectCover(bot);
        Check(
            Physics.LastEye == new Vector3(0, 1.5f, 0),
            "missing known position uses bot position"
        );
        cover.CoverPoints.Clear();
        for (int i = 0; i < 25; i++)
            cover.CoverPoints.Add(new CoverPoint(2) { Usable = false });
        cover.CoverPoints.Add(far);
        Check(VisibleAdapter.SelectCover(bot) == far, "no legacy 24-candidate cap");
        cover.CoverPoints.Clear();
        cover.CoverPoints.Add(new CoverPoint(99));
        Check(VisibleAdapter.SelectCover(bot) == null, "source distance bound");

        var random = new Random(505);
        var counts = PerformanceCapture.Work = new CoverCounts();
        for (int trial = 0; trial < 10000; trial++)
        {
            cover.CoverPoints.Clear();
            Physics.Exposed.Clear();
            CoverPoint expected = null;
            float bestDistance = 99;
            int priorSeasonChecks = 0, priorLinecasts = 0;
            int length = random.Next(20);
            for (int i = 0; i < length; i++)
            {
                int x = random.Next(-4, 12) * 10;
                if (random.Next(6) == 0) Physics.Exposed.Add(x);
                cover.CoverPoints.Add(random.Next(8) == 0 ? null :
                    new CoverPoint(x) { Usable = random.Next(3) != 0 });
            }
            foreach (var point in cover.CoverPoints)
            {
                if (point == null) continue;
                priorSeasonChecks++;
                if (!point.Usable) continue;
                float dist = Vector3.Distance(bot.Position, point.Position);
                if (dist >= bestDistance) continue;
                priorLinecasts++;
                if (Physics.Exposed.Contains(point.Position.x)) continue;
                expected = point;
                bestDistance = dist;
            }
            int beforeRays = Physics.Linecasts;
            long beforeCandidates = counts.CoverCandidates;
            Check(ReferenceEquals(VisibleAdapter.SelectCover(bot), expected),
                "same cover object as seasonal-first selection, trial " + trial);
            Check(Physics.Linecasts - beforeRays == priorLinecasts &&
                counts.CoverCandidates - beforeCandidates == priorSeasonChecks,
                "same threat linecasts and candidate accounting");
        }
        Check(counts.CoverSeasonalChecks < counts.CoverCandidates, "season checks actually skipped");
        Console.WriteLine($"10000 lists: {counts.CoverCandidates} old seasonal checks, {counts.CoverSeasonalChecks} new.");
        Physics.Exposed.Clear();

        foreach (var plan in new[] { ("A01", "COMET:FallBack"), ("A02", "COMET:Cover") })
        {
            state.Feature = plan.Item1;
            state.ActiveLabel = plan.Item2;
            state.SelectedCover = close;
            CoverPoint result = far;
            Check(plugin.Find(cover, ref result) && result == far, "native finder owns the route");
            plugin.Observe(cover, far);
            Check(
                state.SelectedCover == far && plugin.Events.Last() == "native-cover-selected",
                "observe native selection"
            );
            int logged = plugin.Events.Count;
            plugin.Observe(cover, far);
            Check(plugin.Events.Count == logged, "same native point does not repeat diagnostic");
            plugin.Observe(cover, null);
            Check(
                state.SelectedCover == null
                    && plugin.Events.Last() == "native-cover-search-pending",
                "empty cache allows native search to continue"
            );
        }
        var relocating = plugin.Relocation = close;
        CoverPoint chosen = null;
        Check(
            !plugin.Find(cover, ref chosen) && chosen == relocating,
            "explicit relocation still takes ownership"
        );
        ContactSafety.Allowed = false;
        chosen = far;
        Check(
            plugin.Find(cover, ref chosen) && chosen == far,
            "own squad cannot trigger custom movement"
        );
        state.Feature = "other";
        plugin.Observe(cover, close);
        Check(
            state.SelectedCover == null,
            "unrelated plan is not attributed a native cover decision"
        );
        var mirror = new DoorPoint(8.5, 2, 5.5);
        var known = new DoorPoint(10, 2, 18);
        NavMesh.Sample = new Vector3(8.25f, 2.25f, 5.5f);
        Physics.Exposed.Add((float)known.X);
        Check(Plugin.Opposite(mirror, known) == new DoorPoint(8.25, 2.25, 5.5),
            "M14 selects the sampled opening when it can see the remembered contact");
        Check(NavMesh.Query == (new Vector3(8.5f, 2, 5.5f), .6f, NavMesh.AllAreas)
            && Physics.LastEye == NavMesh.Sample.Value + Vector3.up * 1.4f
            && Physics.LastTarget == new Vector3(10, 2 + 1.3f, 18)
            && Physics.LastMask == LayersMaskController.HighPolyWithTerrainMask,
            "M14 uses the native sample radius, eye heights and terrain mask");
        Physics.Exposed.Clear();
        Check(Plugin.Opposite(mirror, known) == null, "blocked sightline falls back to the first opening");
        NavMesh.Sample = null;
        int rays = Physics.Linecasts;
        Check(Plugin.Opposite(mirror, known) == null && Physics.Linecasts == rays,
            "missing NavMesh falls back without a sightline query");
        Console.WriteLine($"{checks} production cover selection and handoff checks passed.");
    }
}

readonly record struct Vector3(float x, float y, float z)
{
    public static Vector3 up => new(0, 1, 0);

    public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.x + b.x, a.y + b.y, a.z + b.z);

    public static Vector3 operator *(Vector3 a, float scale) =>
        new(a.x * scale, a.y * scale, a.z * scale);

    public static float Distance(Vector3 a, Vector3 b) =>
        MathF.Sqrt(
            (a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y) + (a.z - b.z) * (a.z - b.z)
        );
}

readonly record struct DoorPoint(double X, double Y, double Z);

static class NavMesh
{
    public const int AllAreas = -1;
    public static Vector3? Sample;
    public static (Vector3, float, int) Query;
    public readonly record struct Hit(Vector3 position);

    public static bool SamplePosition(Vector3 point, out Hit hit, float radius, int areas)
    {
        Query = (point, radius, areas);
        hit = new(Sample.GetValueOrDefault());
        return Sample.HasValue;
    }
}

static class Physics
{
    internal static HashSet<float> Exposed = new();
    internal static Vector3 LastEye, LastTarget;
    internal static int Linecasts, LastMask;

    public static bool Linecast(Vector3 from, Vector3 to, int mask)
    {
        LastEye = from;
        LastTarget = to;
        LastMask = mask;
        Linecasts++;
        return !Exposed.Contains(to.x);
    }
}

static class LayersMaskController
{
    public const int HighPolyWithTerrainMask = 1;
}

class Enemy
{
    public bool IsVisible;
    public Vector3 EnemyPosition;
    public Places KnownPlaces = new();
}

class Places
{
    public Vector3? LastKnownPosition;
}

class BotComponent
{
    public Vector3 Position;
    public Enemy GoalEnemy;
    public SAINCoverClass Cover = new();
}

class SAINCoverClass
{
    public BotComponent Bot;
    public CoverPoint CoverInUse,
        CoverPoint_MovingTo;
    public List<CoverPoint> CoverPoints = new();
}

class CoverPoint(float x)
{
    public Vector3 Position = new(x, 0, 0);
    public bool Usable = true;
}

class PlanProgress { }

class BotState
{
    public string Feature,
        ActiveLabel;
    public CoverPoint SelectedCover;
}

static class ContactSafety
{
    public static bool Allowed = true;

    public static bool AllowsGoal(BotComponent bot) => Allowed;
}

partial class Plugin
{
    public static Plugin Instance;
    public static DoorPoint? Opposite(DoorPoint mirror, DoorPoint known) => SampleOppositePeek(mirror, known);
    public Dictionary<BotComponent, BotState> states = new();
    public List<string> Events = new();
    public CoverPoint Relocation;

    public static bool SeasonalCoverUsable(BotComponent bot, CoverPoint point) => point.Usable;

    bool FindRelocationCover(SAINCoverClass cover, BotState state, ref CoverPoint result)
    {
        if (Relocation == null)
            return false;
        result = Relocation;
        return true;
    }

    void Emit(BotState state, string stage, string reason) => Events.Add(reason);

    public bool Find(SAINCoverClass cover, ref CoverPoint result) => FindCover(cover, ref result);

    public void Observe(SAINCoverClass cover, CoverPoint result) =>
        ObserveNativeCover(cover, result);
}

static class PerformanceCapture { internal static CoverCounts Work = new(); }
class CoverCounts { internal long CoverCandidates, CoverSeasonalChecks; }
