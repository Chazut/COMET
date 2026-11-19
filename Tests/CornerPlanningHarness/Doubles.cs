using Comet.Core;
using UnityEngine;

namespace UnityEngine
{
    public static class Time
    {
        public static float time;
    }

    public struct Vector3(float x, float y, float z)
    {
        public float x = x,
            y = y,
            z = z;
        public static Vector3 up => new(0, 1, 0);
        public float sqrMagnitude => x * x + y * y + z * z;
        public float magnitude => (float)Math.Sqrt(sqrMagnitude);
        public Vector3 normalized => magnitude > 0 ? this * (1 / magnitude) : default;

        public static Vector3 operator +(Vector3 a, Vector3 b) =>
            new(a.x + b.x, a.y + b.y, a.z + b.z);

        public static Vector3 operator -(Vector3 a, Vector3 b) =>
            new(a.x - b.x, a.y - b.y, a.z - b.z);

        public static Vector3 operator *(Vector3 a, float f) => new(a.x * f, a.y * f, a.z * f);

        public static float Distance(Vector3 a, Vector3 b) => (a - b).magnitude;

        public static Vector3 Cross(Vector3 a, Vector3 b) =>
            new(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
    }
}

namespace Comet.Client
{
    public class BotComponent
    {
        public Vector3 Position;
        public Player Player = new();
        public Owner BotOwner = new();
    }

    public class Player
    {
        public Vector3 Velocity;
        public Movement MovementContext = new();
        public Physical Physical = new();
    }

    public class Owner
    {
        public bool CanSprintPlayer = true;
    }

    public class Movement
    {
        public bool IsGrounded = true,
            CanSprint = true,
            CanJump = true;
    }

    public class Physical
    {
        public Stamina Stamina = new();
    }

    public class Stamina
    {
        public float NormalValue = 1;
    }

    public class BotState
    {
        public Runtime CornerPeek = new();
    }

    public class Runtime
    {
        public CornerPeek Policy = new();
        public float Speed,
            LastSafety,
            NextSafety;
        public Vector3 Origin,
            Near,
            Far,
            Direction;
        public int Candidate,
            PlanStage;
        public bool LaneSafe,
            Open,
            Hidden;
    }

    public partial class Plugin
    {
        public CometConfig config = new();
        public int Queries;
        public bool Open = true,
            Route = true,
            Stop = true;
        public Vector3 LastOrigin;

        private static DoorPoint DP(Vector3 p) => new(p.x, p.y, p.z);

        private static Vector3 VP(DoorPoint p) => new((float)p.X, (float)p.Y, (float)p.Z);

        private void FinishCornerPeek(BotState s) { }

        private void EndCornerPeek(BotState s, string reason) =>
            s.CornerPeek.Policy.End(reason, Time.time);

        private bool CornerOpen(BotComponent b, Vector3 at, Vector3 known)
        {
            Queries++;
            LastOrigin = at;
            return Open;
        }

        private bool CornerRoute(
            BotComponent b,
            Vector3 at,
            Vector3 raw,
            double cap,
            out Vector3 end,
            out object path,
            out string why
        )
        {
            end = raw;
            path = new();
            why = Route ? "" : "blocked";
            return Route;
        }

        private int CombatProtection(Vector3 at, Vector3 known) => 7;

        private string CornerOtherThreats(BotComponent b, Vector3 from, Vector3 to) => "";

        private string RunVolume(BotComponent b, Vector3 from, Vector3 to, bool jump) => "";

        private bool StopCornerRoute(BotState s) => Stop;

        private void RecordCornerPeek(BotState s, bool force = false) { }

        public void Tick(BotComponent b, BotState s, float at)
        {
            Time.time = at;
            PlanCornerPeek(b, s);
        }
    }
}
