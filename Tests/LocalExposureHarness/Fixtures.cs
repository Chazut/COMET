extern alias MathUnity;
global using Color = MathUnity::UnityEngine.Color;
global using Mathf = MathUnity::UnityEngine.Mathf;
global using Vector3 = MathUnity::UnityEngine.Vector3;
using System.Text.Json;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;

namespace UnityEngine
{
    public class Object
    {
        public static Light[] Scene = [];

        public static int Searches;
        public static T[] FindObjectsByType<T>(FindObjectsSortMode _)
        {
            Searches++;
            return (T[])(object)Scene;
        }
    }

    public enum FindObjectsSortMode
    {
        None,
    }

    public enum LightType
    {
        Directional,
        Point,
        Spot,
    }

    public enum QueryTriggerInteraction
    {
        Ignore,
    }

    public class Camera;

    public class GameObject
    {
        public bool activeInHierarchy = true;
    }

    public class Transform
    {
        public Vector3 position,
            forward = Vector3.forward;
    }

    public class Light : Object
    {
        public GameObject gameObject = new();
        public Transform transform = new();
        public LightType type = LightType.Point;
        public bool enabled = true;
        public float intensity = 1f,
            range = 10f,
            spotAngle = 45f;
        public Color color = Color.white;
        public object? Parent,
            Owner;

        public T? GetComponentInParent<T>()
            where T : class => Parent as T;

        public T? GetComponent<T>()
            where T : class => Owner as T;
    }

    public static class Time
    {
        public static float time,
            realtimeSinceStartup;
        public static int frameCount;
    }

    public static class Physics
    {
        public static bool Roof,
            Wall;
        public static int Lines;

        public static bool Raycast(
            Vector3 origin,
            Vector3 direction,
            float distance,
            int mask,
            QueryTriggerInteraction trigger
        ) => Roof;

        public static bool Linecast(
            Vector3 from,
            Vector3 to,
            int mask,
            QueryTriggerInteraction trigger
        )
        {
            Lines++;
            return Wall;
        }
    }

    public static class LightmapSettings
    {
        public static LightProbes? lightProbes;
    }

    public class LightProbes
    {
        public int count = 1;
        public static float Intensity;

        public static void GetInterpolatedProbe(
            Vector3 eye,
            object? renderer,
            out LightProbes probe
        ) => probe = new();

        public void Evaluate(Vector3[] directions, Color[] colors) =>
            Array.Fill(colors, new Color(Intensity, Intensity, Intensity));
    }
}

public class CullingLightObject
{
    public bool IsLightEnabled = true;
    public float CurrentLightIntensity = 1f;
}

public class TOD_Sky
{
    public static TOD_Sky? Instance;
    public bool Initialized = true;
    public Vector3 SunDirection;
}

public static class LayersMaskController
{
    public const int HighPolyWithTerrainMask = 1;
}

namespace EFT
{
    public class Player
    {
        public bool IsYourPlayer;
    }
}

namespace HarmonyLib
{
    public class Harmony
    {
        public void Patch(object method, HarmonyMethod postfix) { }
    }

    public class HarmonyMethod
    {
        public HarmonyMethod(Type owner, string method) { }

        public int priority;
    }

    public static class AccessTools
    {
        public static object Method(Type owner, string method) => method;
    }

    public static class Priority
    {
        public const int Last = 0;
    }
}

namespace SAIN.SAINComponent.Classes.EnemyClasses
{
    public static class EnemyGainSightClass;

    public class Enemy
    {
        public Bot Bot = new();
        public EFT.Player EnemyPlayer = new();
        public bool IsAI,
            IsVisible,
            Seen;
        public float RealDistance = 50f;
        public string EnemyProfileId = "target";
        public Vision Vision = new();
        public Info EnemyInfo = new();
        public Devices EnemyPlayerComponent = new();
        public Body EnemyTransform = new();
    }

    public class Body
    {
        public Vector3 EyePosition = Vector3.up;
    }

    public class Bot
    {
        public string ProfileId = "observer";
        public bool Supported = true;
        public Owner BotOwner = new();
    }

    public class Owner
    {
        public Goggles NightVision = new();
    }

    public class Goggles
    {
        public bool UsingNow;
    }

    public class Info
    {
        public float LastGetHitTime = -10,
            LastDoHitTime = -10;
    }

    public class Vision
    {
        public float TimeLastSeen;
    }

    public class Devices
    {
        public Flashlight Flashlight = new();
    }

    public class Flashlight
    {
        public bool WhiteLight;
    }
}

namespace Comet.Client
{
    public sealed partial class Plugin
    {
        internal static Plugin? Instance;
        internal bool fikaPresent,
            Authority = true;
        internal Settings config = new();
        internal Sink diagnostics = new();
        internal Sink Logger => diagnostics;
        internal int SinkErrors;

        private void WarnFaultSink(Exception _) => SinkErrors++;

        private readonly string session = "raid";

        private bool NativeFixScope(Bot bot) => config.Enabled && Authority && bot.Supported;

        partial void FinishLocalExposure();

        internal void Update() => UpdateExposureDiagnostics();

        internal void End() => FinishLocalExposure();

        internal int Samples => exposure.Samples;

        internal static float Sight(Enemy enemy, float preset)
        {
            ApplyLocalExposure(enemy, ref preset);
            return preset;
        }
    }

    public class Settings
    {
        public bool Enabled = true, EnablePmcs = true, EnableAiPlayerScavs = true,
            LightBasedDetection = true, QuietLogging;
    }

    public class Sink
    {
        public readonly List<string> Lines = new();
        public bool Fail;

        public void Write(string json)
        {
            if (Fail)
                throw new IOException("Test sink offline");
            Lines.Add(json);
        }

        public void WriteEssential(string json) => Write(json);

        public void LogWarning(string message) => throw new Exception(message);
    }

    public static class Diagnostics
    {
        public static string Serialize(object value) => JsonSerializer.Serialize(value);
    }
}
