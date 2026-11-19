using System.Reflection;
using System.Reflection.Emit;
using SAIN.Components;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;

namespace UnityEngine
{
    public struct Vector3(float x, float y, float z)
    {
        public float x = x,
            y = y,
            z = z;

        public static float Distance(Vector3 a, Vector3 b) =>
            MathF.Sqrt(
                (a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y) + (a.z - b.z) * (a.z - b.z)
            );
    }

    public static class Time
    {
        public static float time = 10,
            realtimeSinceStartup = 10;
    }
}

namespace HarmonyLib
{
    public class Harmony
    {
        public List<string> Methods = new();

        public void Patch(
            MethodInfo? m,
            HarmonyMethod? prefix = null,
            HarmonyMethod? postfix = null,
            HarmonyMethod? transpiler = null,
            HarmonyMethod? finalizer = null
        )
        {
            if (m == null)
                throw new Exception("missing hook");
            Methods.Add(m.Name);
        }
    }

    public class HarmonyMethod(Type t, string name) { }

    public class CodeInstruction(OpCode op, object? o = null)
    {
        public OpCode opcode = op;
        public object? operand = o;

        public bool Calls(MethodInfo? m) => Equals(operand, m);
    }

    public static class AccessTools
    {
        const BindingFlags F =
            BindingFlags.Public
            | BindingFlags.NonPublic
            | BindingFlags.Static
            | BindingFlags.Instance;

        public static MethodInfo? Method(Type t, string n, Type[]? a = null) =>
            a == null ? t.GetMethod(n, F) : t.GetMethod(n, F, null, a, null);
    }
}

namespace EFT
{
    public enum EBodyPart
    {
        Head,
    }

    public enum EBotEnemyCause
    {
        checkAddTODO,
    }

    public class Health
    {
        public bool IsAlive = true;
    }

    public interface IPlayer
    {
        string ProfileId { get; }
        Vector3 Position { get; }
        Health HealthController { get; }
    }

    public class Player : IPlayer
    {
        public string ProfileId { get; set; } = "shooter";
        public Vector3 Position { get; set; } = new(70, 0, 0);
        public Health HealthController { get; } = new();
    }

    public class Aggressor
    {
        public IPlayer? iPlayer;
    }

    public struct DamageInfo
    {
        public Aggressor? Player;
    }
}

namespace EFT.Ballistics { }

namespace SAIN.Models.Enums { }

namespace SAIN.Preset.Shared.Enums
{
    public enum SAINSoundType
    {
        Shot,
        GrenadeExplosion,
    }
}

namespace SAIN.Models.Structs
{
    public struct SAINHearingReport
    {
        public Vector3 position;
        public SAIN.Preset.Shared.Enums.SAINSoundType soundType;
        public EEnemyPlaceType placeType;
        public bool isDanger,
            shallReportToSquad;
    }
}

namespace SAIN.Preset
{
    public class BotType
    {
        public List<string>? BrainsToApply;
        public string? BaseBrain;
    }

    public static class BotTypeDefinitions
    {
        public static Dictionary<int, BotType> BotTypes = new();
    }
}

namespace SAIN.SAINComponent.Classes.EnemyClasses
{
    public enum EEnemyPlaceType
    {
        Hearing,
        Injury,
    }

    public class Places
    {
        public List<SAIN.Models.Structs.SAINHearingReport> Reports = new();

        public void UpdatePersonalHeardPosition(
            SAIN.Models.Structs.SAINHearingReport r,
            float time
        ) => Reports.Add(r);
    }

    public class Enemy
    {
        public bool EnemyKnown;
        public Places KnownPlaces = new();
    }

    public class SAINEnemyController(BotComponent b)
    {
        public BotComponent Bot = b;
        public bool RequireGroup;
        public Dictionary<string, Enemy> Enemies = new();

        public Enemy? GetEnemy(string id, bool active) => Enemies.GetValueOrDefault(id);

        public Enemy? CheckAddEnemy(EFT.IPlayer p)
        {
            if (!Bot.BotOwner.BotsGroup.IsPlayerEnemy(p) || Bot.BotOwner.BotsGroup.IsAlly(p))
                return null;
            if (RequireGroup && !Bot.BotOwner.BotsGroup.Enemies.ContainsKey(p))
                return null;
            if (!Enemies.TryGetValue(p.ProfileId, out var e))
                Enemies[p.ProfileId] = e = new();
            return e;
        }
    }
}

namespace SAIN.Components.BotComponentSpace.Classes.EnemyClasses
{
    public class EnemyHearing(BotComponent b)
    {
        public BotComponent Bot = b;

        public void SetHeard(SAIN.Models.Structs.SAINHearingReport report, float time) { }
    }
}

namespace SAIN.SAINComponent.Classes
{
    public class BotHitByEnemyClass(BotComponent b)
    {
        public BotComponent Bot = b;

        public void GetHit(EFT.DamageInfo damage, EFT.EBodyPart part, float amount) { }
    }
}

namespace SAIN.SAINComponent.Classes.Decision
{
    public class BotDecisionManager { }
}

namespace SAIN.Components
{
    public class Group
    {
        public Dictionary<EFT.IPlayer, int> Enemies = new();
        public bool Ally,
            Hostile = true;

        public bool IsPlayerEnemy(EFT.IPlayer p) => Hostile;

        public bool IsAlly(EFT.IPlayer p) => Ally;

        public void AddEnemy(EFT.IPlayer p, EFT.EBotEnemyCause c) => Enemies[p] = 1;
    }

    public class Owner
    {
        public Group BotsGroup = new();
    }

    public class Memory
    {
        public int UnderFire;
        public Vector3 Last;

        public void SetUnderFire(Enemy e, Vector3 p)
        {
            UnderFire++;
            Last = p;
        }
    }

    public class Profile
    {
        public int WildSpawnType = 1;
    }

    public class Info
    {
        public Profile Profile = new();
    }

    public class Squad
    {
        public Dictionary<string, int> Members = new();
    }

    public class BotComponent
    {
        public string ProfileId = "bot";
        public Vector3 Position;
        public bool Supported = true,
            BotActive = true,
            IsDead,
            GameEnding;
        public Owner BotOwner = new();
        public Info Info = new();
        public Memory Memory = new();
        public Squad Squad = new();
        public SAINEnemyController EnemyController;

        public BotComponent()
        {
            EnemyController = new(this);
        }
    }

    public class GrenadeController
    {
        public void RegisterGrenadeExplosionForSAINBots(
            Vector3 explosionPosition,
            EFT.Player player,
            float range
        ) { }
    }
}

namespace Comet.Core
{
    public class CometConfig
    {
        public bool Enabled = true;
        public long Revision = 1;
        public ExplosionAwarenessConfig ExplosionAwareness = new();
        public ImpactReactionConfig ImpactReaction = new();

        public static void Range(double v, double min, double max, string n)
        {
            if (v < min || v > max || !double.IsFinite(v))
                throw new ArgumentException(n);
        }
    }
}

namespace Comet.Client
{
    public static class NativeAdapter
    {
        public static bool IsSupportedAi(BotComponent b) => b.Supported;
    }

    public class Sink
    {
        public List<string> Lines = new();

        public void Write(string s) => Lines.Add(s);

        public void WriteEssential(string s) => Write(s);

        public void Publish(string s, Action<Exception> error) { }
    }

    public static class Api
    {
        public static Sink Decisions = new();
    }

    public static class Diagnostics
    {
        public static string Serialize(object o) => Newtonsoft.Json.JsonConvert.SerializeObject(o);
    }

    public class Log
    {
        public void LogWarning(string s) { }
    }

    internal class BotState
    {
        internal BlastRuntime Blast = new();
        internal ImpactRuntime Impact = new(new());
        internal string ProfileId = "bot",
            Name = "bot",
            Id = "1";
    }

    public sealed partial class Plugin
    {
        internal static Plugin? Instance;
        internal object? world = new();
        internal Comet.Core.CometConfig? config = new();
        internal bool Authority = true;
        internal string session = "source-contact-harness";
        internal long sequence;
        internal bool subscriberWarned;
        internal Sink? diagnostics = new();
        internal Log Logger = new();
        internal HashSet<string> ghosts = new();
        internal Dictionary<BotComponent, BotState> states = new();

        public Plugin()
        {
            Instance = this;
            PatchExplosionAwareness(new());
            PatchImpactReaction(new());
        }

        private bool Ghost(string id) => ghosts.Contains(id);

        private BotState GrenadeState(BotComponent b)
        {
            if (!states.TryGetValue(b, out var s))
                states[b] = s = new();
            return s;
        }

        internal Enemy? Blast(BotComponent b, EFT.Player p, Vector3 position)
        {
            BlastBegin(position, p, 200, out var old);
            try
            {
                return BlastNativeEnemy(b.EnemyController, p.ProfileId, true);
            }
            finally
            {
                BlastFinally(old, null);
            }
        }

        internal void Hit(BotComponent b, EFT.Player p) =>
            ImpactHit(new(b), new() { Player = new() { iPlayer = p } });

        internal void Heard(BotComponent b, EFT.Player p, Vector3 blast, Vector3 report)
        {
            BlastBegin(blast, p, 200, out var old);
            try
            {
                BlastHeard(
                    new(b),
                    new()
                    {
                        position = report,
                        soundType = SAIN.Preset.Shared.Enums.SAINSoundType.GrenadeExplosion,
                    }
                );
            }
            finally
            {
                BlastFinally(old, null);
            }
        }

        internal void End() => ClearBlasts();

        internal bool ContextClear => blastCall == null;

        internal static IEnumerable<HarmonyLib.CodeInstruction> Rewrite(
            IEnumerable<HarmonyLib.CodeInstruction> il
        ) => BlastReportTranspiler(il);
    }
}
