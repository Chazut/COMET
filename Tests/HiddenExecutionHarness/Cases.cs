using System;
using System.Collections.Generic;
using System.Reflection;
using Comet.Core;
using SAIN.Classes.Bot.Search;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using SAIN.Preset.Shared.Models.Enums;
using SAIN.SAINComponent.Classes.Decision;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;
using UnityEngine.AI;
using static SAIN.SAINComponent.Classes.Search.SearchReasonsStruct;

namespace UnityEngine
{
    public static class Time
    {
        public static float time;
        public static int frameCount = 12;
        public static float realtimeSinceStartup => time;
    }

    public struct Vector3
    {
        public float x,
            y,
            z;

        public Vector3(float x, float y, float z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        public float sqrMagnitude => x * x + y * y + z * z;

        public static Vector3 operator -(Vector3 a, Vector3 b) =>
            new(a.x - b.x, a.y - b.y, a.z - b.z);
    }
}

namespace UnityEngine.AI
{
    public enum NavMeshPathStatus
    {
        PathComplete,
        PathPartial,
        PathInvalid,
    }
}

namespace SAIN.Preset.Shared.Enums
{
    public enum ECombatDecision
    {
        None,
        Freeze,
        ShiftCover,
        Search,
        RushEnemy,
        SeekCover,
        ThrowGrenade,
        ShootDistantEnemy,
    }
}

namespace SAIN.Preset.Shared.Models.Enums
{
    public enum ESuppressionState { None, Light, Medium, Heavy, Extreme }
}

namespace SAIN.SAINComponent.Classes.EnemyClasses
{
    public class Place
    {
        public bool HasArrivedPersonal,
            HasArrivedSquad;
    }

    public class Places
    {
        public UnityEngine.Vector3? LastKnownPosition = new UnityEngine.Vector3();
        public Place LastKnownPlace = new();
        public float BotDistanceFromLastKnown = 30;
        public int Searched;

        public void SetPlaceAsSearched(Place p)
        {
            p.HasArrivedPersonal = true;
            Searched++;
        }
    }

    public class Status
    {
        public bool ShotAtMeRecently,
            ShotMeRecently;
        public int NumberOfSearchesStarted;
    }

    public class Path
    {
        public NavMeshPathStatus PathToEnemyStatus;
        public float PathLength = 30;
        public int PathCornerCount = 2;
        public Vector3[] PathCorners = { new(0, 0, 0), new(10, 0, 0) };
    }

    public class Enemy
    {
        public string EnemyProfileId = "enemy";
        public bool IsVisible,
            Seen,
            Heard = true;
        public float RealDistance = 30,
            TimeSinceLastKnownUpdated = 20;
        public Places KnownPlaces = new();
        public Status Status = new();
        public Path Path = new();
    }
}

namespace SAIN.SAINComponent.Classes.Decision
{
    public class EnemyDecisionClass
    {
        public SAIN.Components.BotComponent Bot;
        public bool Shift,
            Rush;
        public bool FinishingDistantShot;
        public string Refusal = "native";
        public int ShiftCalls,
            RushCalls;
        public bool ShiftCoverComplete;
    }
}

namespace SAIN.SAINComponent.Classes.WeaponFunction
{
    public static class SAINBotSuppressClass
    {
        public static float CalcAmmoRatio(Owner o, out int n)
        {
            n = 10;
            return o.Ammo;
        }
    }
}

namespace SAIN.SAINComponent.Classes.Search
{
    public static class SearchReasonsStruct
    {
        public enum ECantStartReason
        {
            None,
            Suppressed,
        }

        public enum EWantToSearchReason
        {
            None,
            ContinueSearch_EnemyNotHeard,
            NewSearch_EnemyNotHeard,
        }

        public enum ENotWantToSearchReason
        {
            None,
            WontSearchFromAudio,
            CantStart,
            ShallNotSearch,
            Arrived,
        }

        public struct WantSearchReasonsStruct
        {
            public ECantStartReason CantStartReason;
            public EWantToSearchReason WantToSearchReason;
            public ENotWantToSearchReason NotWantToSearchReason;
        }
    }
}

namespace SAIN.Classes.Bot.Search
{
    public class Reasons
    {
        public string PathCalcFailReason = "",
            NotSearchReason = "native";
        public WantSearchReasonsStruct WantSearchReasons;
    }

    public class SearchDecider
    {
        public BotComponent Bot;
        public bool Allowed = true,
            StartAllowed = true,
            WantAllowed = true;
        public int Calls,
            StartCalls,
            WantCalls;
        public Reasons Reasons = new();

        public bool ShallStartSearch(Enemy e, out Reasons r)
        {
            Calls++;
            r = Reasons;
            return Allowed;
        }

        public static void ShallContinueSearch() { }

        public static void shallBeginSearch() { }
    }

    public class SearchPathFinder
    {
        public BotComponent Bot;
        public Place TargetPlace;
        public int Resets;

        public void Reset()
        {
            TargetPlace = null;
            Resets++;
        }

        public bool CheckEnemyPath(Enemy e, out string reason)
        {
            TargetPlace = e.KnownPlaces.LastKnownPlace;
            reason = "";
            return true;
        }
    }

    public class SearchClass
    {
        public string CurrentState = "None";
        public BotComponent Bot;
        public SearchDecider Decider = new();
        public SearchPathFinder PathFinder = new();
    }
}

namespace SAIN.Components
{
    public class MovePath
    {
        public string Status = "Moving";
        public Vector3 Destination;
    }

    public class Mover
    {
        public MovePath ActivePath = new();
        public bool Moving;
    }

    public class BotComponent
    {
        public Suppression Suppression = new();
        public Mover Mover = new();
        public Vector3 Position;
        public bool Supported = true,
            BotActive = true,
            GameEnding;
        public string ProfileId = "bot";
        public Enemy GoalEnemy = new();
        public Decision Decision = new();
        public Cover Cover = new();
        public Owner BotOwner = new();
        public Medical Medical = new();
        public Enemies EnemyController = new();
        public Info Info = new();
        public SearchClass Search = new();

        public BotComponent()
        {
            Search.Bot = this;
            Search.Decider.Bot = this;
            Search.PathFinder.Bot = this;
        }
    }

    public class Suppression
    {
        public ESuppressionState CurrentState;
    }

    public class Decision
    {
        public ECombatDecision CurrentCombatDecision;
        public EnemyDecisionClass EnemyDecisions = new();
    }

    public class Cover
    {
        public object CoverInUse;
        public List<object> CoverPoints = new() { new() };
    }

    public class Owner
    {
        public Memory Memory = new();
        public float Ammo = .35f;
    }

    public class Memory
    {
        public bool IsUnderFire;
    }

    public class Medical
    {
        public float TimeSinceShot = 100;
    }

    public class Enemies
    {
        public List<Enemy> KnownEnemies = new();
    }

    public class Info
    {
        public string Personality = "Normal";
        public Settings PersonalitySettings = new();
        public float TimeBeforeSearch = 20;
    }

    public class Settings
    {
        public SearchSettings Search = new();
    }

    public class SearchSettings
    {
        public bool WillSearchForEnemy = true,
            WillSearchFromAudio;
    }
}

namespace Comet.Client.Api
{
    public static class Decisions
    {
        public static List<string> Sent = new();

        public static void Publish(string json, Action<Exception> failure) => Sent.Add(json);
    }
}

namespace Comet.Client
{
    public static class Diagnostics
    {
        public static string Serialize(object value) =>
            System.Text.Json.JsonSerializer.Serialize(value);
    }

    public class Writer
    {
        public bool Quiet,
            Fail;
        public List<string> Reasons = new(),
            Saved = new();

        public void Write(string json)
        {
            if (Fail)
                throw new Exception("writer offline");
            if (!Quiet)
                Saved.Add(json);
            Reasons.Add(
                System
                    .Text.Json.JsonDocument.Parse(json)
                    .RootElement.GetProperty("reason")
                    .GetString()
            );
        }
    }

    class CoverPoint
    {
        public string StraightDistanceStatus = "Far";
        public bool BotInThisCover;
    }

    class FlankRuntime
    {
        public CoverPoint Point = new();
    }

    class TestDoor
    {
        public bool NativeFallback, NativeSelected, Fault;
        public string SessionPlan = "";
    }
    class TestHandoff
    {
        public bool Cleared;
        public void Clear() => Cleared = true;
    }
    class TestLog { public void LogWarning(string message) => throw new Exception(message); }
    class BotState
    {
        public TestDoor Door = new();
        public TestHandoff Handoff = new();
        public ECombatDecision Expected;
        public int RequestedFrame = -1;
        public string Id = "bot-instance",
            ProfileId = "bot",
            Name = "Back4Streetz",
            Decision = "Flank";
        public FlankRuntime Flank = new();
        public HiddenExecutionRuntime HiddenExecution = new();
        public HiddenEnemyDecision Engine = new(new CometConfig());
    }

    static class NativeAdapter
    {
        public static bool IsSupportedAi(BotComponent b) => b.Supported;

        public static bool Throw(BotComponent b, Enemy e, out string reason)
        {
            reason = "";
            return true;
        }

        public static ECombatDecision Map(Tactic t) =>
            t switch
            {
                Tactic.Hold => ECombatDecision.Freeze,
                Tactic.Search => ECombatDecision.Search,
                Tactic.Flank => ECombatDecision.ShiftCover,
                Tactic.Push => ECombatDecision.RushEnemy,
                Tactic.FallBack => ECombatDecision.SeekCover,
                _ => ECombatDecision.ThrowGrenade,
            };
    }

    static class ContactSafety
    {
        public static bool Allowed = true;

        public static bool Allows(BotComponent b, Enemy e) => Allowed;
    }

    static class ZzapEnemyWeakness
    {
        internal static float Weakness;
        internal static float Score(BotComponent b, Enemy e) => Weakness;
    }

    public partial class Plugin
    {
        TestLog Logger = new();
        internal bool SelectedDoor, NearbyDoor;
        internal bool CanResume(BotComponent b, VisibleChoice visible = null) =>
            HiddenSightMayTakeOver(b, b.GoalEnemy, visible);
        internal ECombatDecision NativeFallback(BotComponent b, bool rush)
        {
            var d = b.Decision.EnemyDecisions;
            d.Bot = b;
            AfterNativeDoorRush(d, b.GoalEnemy, ref rush);
            var result = rush ? ECombatDecision.RushEnemy : ECombatDecision.SeekCover;
            FinishNativeDoorChoice(d, ref result);
            return result;
        }
        bool EnsureDoorSession(BotComponent b, Enemy enemy, BotState state, bool search) => SelectedDoor;
        object NearestTacticalDoor(BotComponent b, Enemy enemy, BotState state, Vector3 known) => NearbyDoor ? this : null;
        public static Plugin Instance;
        public CometConfig config = new();
        public object world = new();
        public bool Authority = true,
            IsGhost,
            OtherSees;
        internal Dictionary<BotComponent, BotState> states = new();
        public Writer diagnostics = new();
        public string session = "test";
        public long sequence;
        public List<string> Events => diagnostics.Reasons;

        bool Ghost(string id) => IsGhost;

        static DoorPoint DP(Vector3 v) => new(v.x, v.y, v.z);

        static bool SourceOtherEnemyMaySeeMe(BotComponent b, Enemy e) => Instance.OtherSees;

        internal void Log(BotComponent b, BotState s, string reason) =>
            RecordHiddenExecution(b, s, reason);

        internal string Can(BotComponent b, BotState s, Tactic t) =>
            HiddenCanRun(b, b.GoalEnemy, s, t, HiddenBeingShot(b));

        internal Choice Keep(BotComponent b, BotState s, Choice c) =>
            KeepHiddenChoice(b, b.GoalEnemy, s, c, HiddenBeingShot(b));

        internal void Watch(BotComponent b, BotState s) => WatchHiddenMovement(b, b.GoalEnemy, s);

        public void Partial(BotComponent b) =>
            FinishPartialSearch(b.Search.PathFinder, b.GoalEnemy);

        public bool Quiet(BotComponent b) => QuietHeardSearch(b, b.GoalEnemy);

        public bool Walk(BotComponent b)
        {
            bool sprint = true;
            WalkQuietSearch(b.Search, b.GoalEnemy, ref sprint);
            return sprint;
        }

        public bool Audio(BotComponent b, ref WantSearchReasonsStruct reason)
        {
            bool result = false;
            QuietAudioSearch(b.Search.Decider, b.GoalEnemy, ref reason, ref result);
            return result;
        }

        public bool Continue(BotComponent b, bool continuing, out EWantToSearchReason reason)
        {
            bool result = false;
            reason = EWantToSearchReason.None;
            QuietSearchAllowed(
                b.Search.Decider,
                b.GoalEnemy,
                typeof(SearchDecider).GetMethod(
                    continuing ? "ShallContinueSearch" : "shallBeginSearch"
                ),
                ref reason,
                ref result
            );
            return result;
        }

        public static void Bind()
        {
            nativeDistantShot = (EnemyDecisionClass d, Enemy e, out string r) =>
            {
                r = "";
                return d.FinishingDistantShot;
            };
            nativeShift = (EnemyDecisionClass d, Enemy e, out string r) =>
            {
                d.ShiftCalls++;
                r = d.Refusal;
                return d.Shift;
            };
            nativeRush = (EnemyDecisionClass d, Enemy e, out string r) =>
            {
                d.RushCalls++;
                r = d.Refusal;
                return d.Rush;
            };
            canStartSearch = (SearchDecider d, Enemy e, out ECantStartReason r) =>
            {
                d.StartCalls++;
                r = d.StartAllowed ? ECantStartReason.None : ECantStartReason.Suppressed;
                return d.StartAllowed;
            };
            shallSearch = (SearchDecider d, Enemy e, out EWantToSearchReason r) =>
            {
                d.WantCalls++;
                r = EWantToSearchReason.NewSearch_EnemyNotHeard;
                return d.WantAllowed;
            };
        }
    }

    public static class Cases
    {
        static int checks;

        static void Check(bool ok, string why)
        {
            checks++;
            if (!ok)
                throw new Exception(why);
        }

        static (Plugin, BotComponent, BotState) Setup()
        {
            Time.time = 10;
            ContactSafety.Allowed = true;
            var p = Plugin.Instance = new Plugin();
            Plugin.Bind();
            var b = new BotComponent();
            var s = new BotState();
            p.states[b] = s;
            return (p, b, s);
        }

        public static void Run()
        {
            {
                var (p, b, _) = Setup();
                Check(p.CanResume(b), "hidden memory may take over an unclaimed visible contact");
                Check(!p.CanResume(b, new VisibleChoice { Decision = VisibleTactic.Cover }),
                    "an executable visible choice takes precedence over hidden memory");
                b.Decision.CurrentCombatDecision = ECombatDecision.ShootDistantEnemy;
                b.Decision.EnemyDecisions.FinishingDistantShot = true;
                Check(!p.CanResume(b), "SAIN retains its active distant shot");
                b.Decision.EnemyDecisions.FinishingDistantShot = false;
                Check(p.CanResume(b), "the old decision name cannot block an expired shot");
                foreach (ESuppressionState level in Enum.GetValues(typeof(ESuppressionState)))
                {
                    b.Suppression.CurrentState = level;
                    Check(p.CanResume(b) == (level <= ESuppressionState.Medium),
                        "visible fallback respects the source aggressive branch");
                }
            }
            {
                var (p, b, s) = Setup();
                p.SelectedDoor = true;
                s.Door.SessionPlan = "clear";
                Check(p.NativeFallback(b, false) == ECombatDecision.SeekCover, "unarmed native fallback stays native");
                s.Door.NativeFallback = true;
                Check(p.NativeFallback(b, true) == ECombatDecision.RushEnemy, "native rush wins before tactical door check");
                s.Door.NativeFallback = true;
                Check(p.NativeFallback(b, false) == ECombatDecision.Search && s.Handoff.Cleared
                    && s.RequestedFrame == Time.frameCount && !s.Door.NativeSelected, "native declined rush hands selected clear to SearchAction once");
                s.Door.SessionPlan = "trap";
                s.Door.NativeFallback = true;
                Check(p.NativeFallback(b, false) == ECombatDecision.Freeze && s.Decision == "Hold", "combat trap uses FreezeAction");
            }
            {
                var (p, b, s) = Setup();
                Api.Decisions.Sent.Clear();
                s.HiddenExecution.Refusals["Search"] = "partial";
                s.HiddenExecution.Refusals["Flank"] = "native-ended";
                p.Log(b, s, "hidden-none-runnable");
                Check(
                    p.diagnostics.Saved.Count == 1
                        && Api.Decisions.Sent.Count == 1
                        && p.diagnostics.Saved[0].Contains("native-ended")
                        && p.diagnostics.Saved[0].Contains("partial"),
                    "single diagnostic includes every refusal and reaches RR"
                );
                for (int i = 0; i < 29; i++)
                {
                    Time.time = 10 + i * .1f;
                    p.Log(b, s, "alternating-" + i);
                }
                Check(
                    p.diagnostics.Saved.Count == 1,
                    "changing refusal reasons cannot defeat three second throttle"
                );
                p.diagnostics.Quiet = true;
                Time.time = 13;
                p.Log(b, s, "quiet");
                Check(
                    p.diagnostics.Saved.Count == 1 && Api.Decisions.Sent.Count == 2,
                    "Quiet filters disk while preserving RR publication"
                );
                p.diagnostics.Fail = true;
                Time.time = 16;
                p.Log(b, s, "offline");
                Check(true, "diagnostic failure never escapes into gameplay");
            }
            {
                var (p, b, s) = Setup();
                foreach (
                    var reason in new[] { "dontWantTo", "suppressed", "cantShift", "native-ended" }
                )
                {
                    b.Decision.EnemyDecisions.Refusal = reason;
                    Check(
                        p.Can(b, s, Tactic.Flank) == "native-shift:" + reason,
                        "native ShiftCover refusal controls flank: " + reason
                    );
                }
                b.Decision.EnemyDecisions.Shift = true;
                Check(
                    p.Can(b, s, Tactic.Flank) == "" && b.Decision.EnemyDecisions.ShiftCalls == 5,
                    "native continuation accepted without another geometry veto"
                );
                b.Search.Decider.Allowed = false;
                b.Search.Decider.Reasons.PathCalcFailReason = "partial";
                Check(
                    p.Can(b, s, Tactic.Search).Contains("partial")
                        && b.GoalEnemy.Status.NumberOfSearchesStarted == 0,
                    "refused search not counted as a start"
                );
                b.Search.Decider.Allowed = true;
                Check(
                    p.Can(b, s, Tactic.Search) == ""
                        && b.GoalEnemy.Status.NumberOfSearchesStarted == 1,
                    "source start count incremented"
                );
                b.Decision.CurrentCombatDecision = ECombatDecision.Search;
                p.Can(b, s, Tactic.Search);
                Check(
                    b.GoalEnemy.Status.NumberOfSearchesStarted == 1,
                    "continuation not counted as new search"
                );
                Check(p.Can(b, s, Tactic.Push) == "", "source 35 percent/40m fallback allowed");
                p.NearbyDoor = true;
                Check(p.Can(b, s, Tactic.Push) == "zzap-push-not-through-door", "ordinary push cannot charge a strong enemy through a refused door");
                ZzapEnemyWeakness.Weakness = .5f;
                Check(p.Can(b, s, Tactic.Push) == "", "weakness threshold allows ordinary push");
                ZzapEnemyWeakness.Weakness = 0;
                b.Info.Personality = "Wreckless";
                Check(p.Can(b, s, Tactic.Push) == "", "reckless personality may rush past the door");
                b.Info.Personality = "Normal";
                p.SelectedDoor = true;
                Check(p.Can(b, s, Tactic.Push) == "", "selected combat door is taken before ordinary push");
                p.NearbyDoor = p.SelectedDoor = false;
                b.BotOwner.Ammo = .3499f;
                Check(p.Can(b, s, Tactic.Push) != "", "source ammo threshold");
                b.BotOwner.Ammo = .35f;
                b.Info.Personality = "Coward";
                Check(p.Can(b, s, Tactic.Push) != "", "Coward cannot bypass native rush refusal");
                b.Decision.EnemyDecisions.Rush = true;
                Check(p.Can(b, s, Tactic.Push) == "", "Coward can use accepted native rush");
                b.GoalEnemy.KnownPlaces.BotDistanceFromLastKnown = 2.49f;
                Check(
                    p.Can(b, s, Tactic.Push) == "zzap-push-nowhere",
                    "source straight-distance guard"
                );
            }
            {
                var (p, b, s) = Setup();
                p.OtherSees = true;
                Check(
                    p.Can(b, s, Tactic.Hold) == "zzap-hold-other-enemy",
                    "secondary threat blocks Hold"
                );
                p.OtherSees = false;
                Time.time = 13.99f;
                Check(p.Can(b, s, Tactic.Hold) != "", "four second source window retained");
                Time.time = 14;
                Check(p.Can(b, s, Tactic.Hold) == "", "window not renewed each tick");
                b.EnemyController.KnownEnemies.Add(
                    new() { Status = new() { ShotAtMeRecently = true } }
                );
                Check(
                    p.Can(b, s, Tactic.Hold) == "zzap-hold-under-fire",
                    "recent shots before native under-fire flag respected"
                );
                b.Cover.CoverPoints.Clear();
                Check(
                    p.Can(b, s, Tactic.FallBack) == "zzap-fallback-no-cover",
                    "native cover availability"
                );
            }
            foreach (
                var tactic in new[]
                {
                    Tactic.Hold,
                    Tactic.Search,
                    Tactic.Push,
                    Tactic.FallBack,
                    Tactic.Flank,
                    Tactic.Grenade,
                }
            )
            {
                var (p, b, s) = Setup();
                p.Keep(b, s, new() { Decision = tactic });
                b.Decision.CurrentCombatDecision = NativeAdapter.Map(tactic);
                Time.time = 11.99f;
                Check(
                    p.Keep(b, s, new()).Decision == (tactic == Tactic.Grenade ? null : tactic),
                    "keep source actions only"
                );
                Time.time = 12;
                Check(
                    p.Keep(b, s, new()).Decision == null,
                    "keep expires after exactly two seconds, no sliding deadline"
                );
            }
            {
                var (p, b, s) = Setup();
                p.Keep(b, s, new() { Decision = Tactic.Flank });
                b.Decision.CurrentCombatDecision = ECombatDecision.ShiftCover;
                b.GoalEnemy = new() { RealDistance = 16 };
                Check(
                    p.Keep(b, s, new()).Decision == Tactic.Flank,
                    "unseen far target swap can retain source action"
                );
                b.GoalEnemy.RealDistance = 15;
                Check(p.Keep(b, s, new()).Decision == null, "near target swap does not retain");
                b.GoalEnemy.RealDistance = 30;
                b.Medical.TimeSinceShot = 1;
                Check(
                    p.Keep(b, s, new()).Decision == null,
                    "being shot interrupts keep immediately"
                );
            }
            {
                var (p, b, s) = Setup();
                b.Decision.CurrentCombatDecision = ECombatDecision.Search;
                p.Watch(b, s);
                Time.time = 16;
                p.Watch(b, s);
                Check(
                    b.GoalEnemy.KnownPlaces.LastKnownPlace.HasArrivedPersonal
                        && p.Events.Contains("zzap-move-stalled-Search"),
                    "source stalled search marks visited with diagnostic"
                );
                Check(
                    p.Can(b, s, Tactic.Search) == "zzap-search-stalled"
                        && p.Can(b, s, Tactic.Push) == "zzap-push-nowhere",
                    "source watchdog gates both moving stances"
                );
            }
            {
                var (p, b, s) = Setup();
                Check(p.Quiet(b) && !p.Walk(b), "quiet audio fallback walks");
                var r = new WantSearchReasonsStruct
                {
                    NotWantToSearchReason = ENotWantToSearchReason.WontSearchFromAudio,
                };
                b.Search.Decider.StartAllowed = false;
                Check(
                    !p.Audio(b, ref r)
                        && r.NotWantToSearchReason == ENotWantToSearchReason.CantStart
                        && b.Search.Decider.WantCalls == 0,
                    "quiet fallback cannot bypass native heavy suppression"
                );
                r.NotWantToSearchReason = ENotWantToSearchReason.WontSearchFromAudio;
                b.Search.Decider.StartAllowed = true;
                Check(
                    p.Audio(b, ref r) && r.NotWantToSearchReason == ENotWantToSearchReason.None,
                    "quiet resumes native remaining checks"
                );
                r.NotWantToSearchReason = ENotWantToSearchReason.Arrived;
                int calls = b.Search.Decider.StartCalls;
                Check(
                    !p.Audio(b, ref r) && b.Search.Decider.StartCalls == calls,
                    "visited contact refusal not overridden"
                );
                Check(
                    p.Continue(b, true, out var cr)
                        && cr == EWantToSearchReason.ContinueSearch_EnemyNotHeard
                        && p.Continue(b, false, out cr)
                        && cr == EWantToSearchReason.NewSearch_EnemyNotHeard,
                    "quiet source begin and continuation reasons"
                );
            }
            foreach (
                string scope in new[]
                {
                    "solo",
                    "fika-host",
                    "fika-client",
                    "ghost",
                    "disabled",
                    "unsupported",
                    "inactive",
                    "ended",
                    "no-world",
                    "own-squad",
                }
            )
            {
                var (p, b, s) = Setup();
                switch (scope)
                {
                    case "fika-client":
                        p.Authority = false;
                        break;
                    case "ghost":
                        p.IsGhost = true;
                        break;
                    case "disabled":
                        p.config.Enabled = false;
                        break;
                    case "unsupported":
                        b.Supported = false;
                        break;
                    case "inactive":
                        b.BotActive = false;
                        break;
                    case "ended":
                        b.GameEnding = true;
                        break;
                    case "no-world":
                        p.world = null;
                        break;
                    case "own-squad":
                        ContactSafety.Allowed = false;
                        break;
                }
                bool allowed = scope is "solo" or "fika-host";
                b.GoalEnemy.Path.PathToEnemyStatus = NavMeshPathStatus.PathPartial;
                b.Position = new(8, 0, 0);
                b.Search.PathFinder.TargetPlace = b.GoalEnemy.KnownPlaces.LastKnownPlace;
                Check(p.Quiet(b) == allowed, "quiet search authority/lifetime scope: " + scope);
                p.Partial(b);
                Check(
                    (b.GoalEnemy.KnownPlaces.Searched == 1) == allowed,
                    "partial completion authority/lifetime scope: " + scope
                );
            }
            foreach (
                string guard in new[]
                {
                    "complete",
                    "invalid",
                    "no-corners",
                    "far",
                    "new-place",
                    "visited",
                    "valid",
                }
            )
            {
                var (p, b, s) = Setup();
                b.GoalEnemy.Path.PathToEnemyStatus = NavMeshPathStatus.PathPartial;
                b.Position = new(8, 0, 0);
                b.Search.PathFinder.TargetPlace = b.GoalEnemy.KnownPlaces.LastKnownPlace;
                switch (guard)
                {
                    case "complete":
                        b.GoalEnemy.Path.PathToEnemyStatus = NavMeshPathStatus.PathComplete;
                        break;
                    case "invalid":
                        b.GoalEnemy.Path.PathToEnemyStatus = NavMeshPathStatus.PathInvalid;
                        break;
                    case "no-corners":
                        b.GoalEnemy.Path.PathCornerCount = 0;
                        break;
                    case "far":
                        b.Position = new(7.99f, 0, 0);
                        break;
                    case "new-place":
                        b.GoalEnemy.KnownPlaces.LastKnownPlace = new();
                        break;
                    case "visited":
                        b.GoalEnemy.KnownPlaces.LastKnownPlace.HasArrivedSquad = true;
                        break;
                }
                p.Partial(b);
                Check(
                    b.Search.PathFinder.Resets == (guard == "valid" ? 1 : 0),
                    "source partial-end guard: " + guard
                );
                if (guard == "valid")
                    Check(
                        p.Events.Contains("zzap-search-partial-end-reached"),
                        "partial completion observable"
                    );
            }
            Console.WriteLine(
                $"Hidden execution/search production adapters: {checks} checks passed."
            );
        }
    }
}
