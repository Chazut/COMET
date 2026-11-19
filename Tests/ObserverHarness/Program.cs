using Comet.ObserverProbe;

int checks = 0;
void Check(bool ok, string message)
{
    checks++;
    if (!ok)
        throw new Exception("I01 production observer: " + message);
}
for (int scope = 0; scope < 6; scope++)
for (int lane = 0; lane < 4; lane++)
{
    var p = new Plugin();
    var b = new Bot();
    var s = new BotState();
    var d = new BotDecisionManager { Bot = b };
    p.Track(b, s);
    p.Special = lane;
    s.Decision = "Hold";
    s.Feature = "A01";
    s.Expected = ECombatDecision.Freeze;
    s.RequestedFrame = Time.frameCount;
    if (scope == 1)
        p.config!.Enabled = false;
    if (scope == 2)
        s.Retired = true;
    if (scope == 3)
        b.Pmc = false;
    if (scope == 4)
        p.Authority = false;
    if (scope == 5)
        p.IsGhost = true;
    d.CurrentCombatDecision = ECombatDecision.Freeze;
    p.Observe(d);
    if (scope > 0)
        Check(
            p.SpecialCalls == 0 && s.RequestedFrame == -1 && s.Decision.Length == 0,
            "scope gates all lanes"
        );
    else if (lane > 0)
        Check(s.RequestedFrame == -1, "special lane consumes common stale ticket");
    else
    {
        Check(
            s.ActiveLabel == "COMET:Hold" && p.Events.Any(e => e.Stage == "accepted"),
            "actual acceptance"
        );
        Check(
            s.Execution.Active && s.Execution.Action == "Hold",
            "acceptance starts independent execution observation"
        );
    }
}
foreach (string priority in new[] { "other-action", "self", "squad", "stale", "none" })
{
    var p = new Plugin();
    var b = new Bot();
    var s = new BotState
    {
        Decision = "Push",
        Feature = "A01",
        Expected = ECombatDecision.RushEnemy,
        RequestedFrame = 100,
    };
    p.Track(b, s);
    var d = new BotDecisionManager { Bot = b, CurrentCombatDecision = ECombatDecision.RushEnemy };
    if (priority == "other-action")
        d.CurrentCombatDecision = ECombatDecision.Freeze;
    if (priority == "self")
        d.CurrentSelfDecision = ESelfActionType.Reload;
    if (priority == "squad")
        d.CurrentSquadDecision = ESquadDecision.Help;
    if (priority == "stale")
        s.RequestedFrame = 99;
    if (priority == "none")
        s.Decision = "";
    p.Observe(d);
    Check(
        s.RequestedFrame == -1 && s.ActiveLabel.Length == 0,
        "refused request consumed " + priority
    );
    Check(!p.Events.Any(e => e.Stage == "accepted"), "no false acceptance " + priority);
    if (priority != "none")
        Check(
            s.Decision.Length == 0 && p.Events.Count(e => e.Stage == "rejected") == 1,
            "unlabeled selection explicitly rejected " + priority
        );
    int count = p.Events.Count;
    p.Observe(d);
    Check(p.Events.Count == count, "no repeated rejection each tick");
}
{
    var p = new Plugin();
    var b = new Bot();
    var s = new BotState
    {
        Decision = "Push",
        Feature = "A01",
        Expected = ECombatDecision.RushEnemy,
        RequestedFrame = 100,
    };
    p.Track(b, s);
    var d = new BotDecisionManager { Bot = b, CurrentCombatDecision = ECombatDecision.RushEnemy };
    p.Observe(d);
    Check(s.ActiveLabel.Length > 0, "first accepted");
    p.Observe(d);
    Check(
        s.ActiveLabel.Length == 0 && s.Decision.Length == 0 && !s.Execution.Active,
        "native same-frame repeat does not inherit COMET ownership"
    );
    Check(p.Events.Count(e => e.Stage == "accepted") == 1, "one acceptance");
}
Console.WriteLine(
    $"I01: {checks} checks of the production Accepted observer, with native/Unity test doubles."
);

namespace Comet.ObserverProbe
{
    using Comet.Core;

    static class Time
    {
        public static int frameCount = 100;
        public static float time = 5;
    }

    enum ECombatDecision
    {
        None,
        Freeze,
        RushEnemy,
    }

    enum ESelfActionType
    {
        None,
        Reload,
    }

    enum ESquadDecision
    {
        None,
        Help,
    }

    sealed class Bot
    {
        public bool Pmc = true,
            BotActive = true;
        public Act Shoot = new(),
            ManualShoot = new(),
            Aim = new();
    }

    sealed class Act
    {
        public void EndShoot() { }

        public void Reset() { }

        public void LoseAimTarget() { }
    }

    sealed class BotDecisionManager
    {
        public Bot Bot = null!;
        public ECombatDecision CurrentCombatDecision;
        public ESelfActionType CurrentSelfDecision;
        public ESquadDecision CurrentSquadDecision;
    }

    static class NativeAdapter
    {
        public static bool IsSupportedAi(Bot b) => b.Pmc;
    }

    sealed class Settings
    {
        public bool Enabled = true;
        public Visible VisibleCombat = new();
    }

    sealed class Visible
    {
        public float CoverMemory = 2;
    }

    sealed class Situation
    {
        public bool Visible;
        public string Target = "target";
    }

    sealed class Intent
    {
        public void Accepted(string target, float now, float memory) { }

        public void Clear() { }
    }

    sealed class Handoff
    {
        public bool Yielded;

        public void Accepted(float now, string feature, string decision, object cue) { }

        public bool ObserveNative(float now, string combat, string self, string squad) => false;
    }

    sealed class BotState
    {
        public bool Retired;
        public string Decision = "",
            Feature = "",
            ProfileId = "pmc",
            ActiveLabel = "";
        public int RequestedFrame = -1;
        public ECombatDecision Expected;
        public Handoff Handoff = new();
        public Situation Situation = new();
        public Intent Intent = new();
        public DecisionExecution Execution = new();
    }

    static class Api
    {
        public static class Decisions
        {
            public static void Set(string profile, string label) { }
        }
    }

    sealed class Log
    {
        public void LogWarning(string message) => throw new Exception(message);
    }

    sealed record Event(string Stage, string Reason);

    partial class Plugin
    {
        static Plugin? Instance;
        readonly Dictionary<Bot, BotState> states = new();
        public Settings? config = new();
        public bool Authority = true,
            IsGhost;
        public int Special,
            SpecialCalls;
        public List<Event> Events = new();
        Log Logger = new();

        public void Track(Bot b, BotState s)
        {
            Instance = this;
            states.Add(b, s);
        }

        public void Observe(BotDecisionManager d) => Accepted(d);

        bool Ghost(string profile) => IsGhost;

        // Service arbitration is covered by ControlHarness; this harness exercises acceptance without a service.
        bool ObservePlanService(Bot bot, BotState state, bool observe) => false;

        bool FeintAccepted(BotDecisionManager d, BotState s)
        {
            SpecialCalls++;
            return Special == 1;
        }

        bool RelocationAccepted(BotDecisionManager d, BotState s)
        {
            SpecialCalls++;
            return Special == 2;
        }

        bool ImpactDecisionObserved(BotDecisionManager d, BotState s)
        {
            SpecialCalls++;
            return Special == 3;
        }

        void SilentDecisionObserved(BotDecisionManager d, BotState s) { }

        void SupportAccepted(BotState s, bool applied) { }

        void DoorResumeAccepted(BotState s, bool applied) { }

        void DoorEmergencyAccepted(BotState s, bool applied) { }

        static object HandoffContact(Situation s) => s;

        void Emit(BotState s, string stage, string reason, string feature = "") =>
            Events.Add(new(stage, reason));

        void Release(BotState s, string reason)
        {
            s.RequestedFrame = -1;
            s.Decision = "";
            s.ActiveLabel = "";
            s.Execution.Clear();
        }

        void YieldToSain(BotState s, string reason, string kind = "")
        {
            Emit(s, "released", reason);
            Release(s, reason);
        }
    }
}
