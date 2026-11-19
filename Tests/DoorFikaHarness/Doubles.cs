using Comet.Fika;
using Fika.Core.Networking.LiteNetLib;

namespace BepInEx.Logging
{
    public class ManualLogSource
    {
        public void LogWarning(string text) => Console.WriteLine(text);
    }
}

namespace UnityEngine
{
    public struct Vector3(float x, float y, float z)
    {
        public float x = x,
            y = y,
            z = z;

        public static float Distance(Vector3 a, Vector3 b) =>
            (float)
                Math.Sqrt(Math.Pow(a.x - b.x, 2) + Math.Pow(a.y - b.y, 2) + Math.Pow(a.z - b.z, 2));
    }

    public static class Time
    {
        public static float unscaledTime;
    }
}

namespace EFT.Interactive
{
    public class WorldInteractiveObject
    {
        public string Id = "door";
    }
}

namespace Fika.Core.Main.Players
{
    public class FikaPlayer : EFT.Player
    {
        public void ExecuteInteraction(EFT.Interactive.WorldInteractiveObject door) { }
    }
}

namespace HarmonyLib
{
    public class Harmony(string id)
    {
        public void Patch(System.Reflection.MethodInfo? method, HarmonyMethod postfix) { }

        public void UnpatchSelf() { }
    }

    public class HarmonyMethod(Type t, string method) { }

    public static class AccessTools
    {
        public static System.Reflection.MethodInfo? DeclaredMethod(Type t, string name) =>
            t.GetMethod(name);
    }
}

namespace Comet.Api
{
    public static class DoorMotionNetwork
    {
        public static Func<bool>? Ready;
        public static event Action<string, int, float>? Changed;
        public static event Action? RaidEnded;
        public static bool Available = true;

        public static bool DoorAvailable(string id) => Available;

        public static List<(string, int, float)> Applied = new();

        public static bool Apply(string id, int state, float angle)
        {
            Applied.Add((id, state, angle));
            return true;
        }

        public static void Change(string id, int state, float angle) =>
            Changed?.Invoke(id, state, angle);

        public static void End() => RaidEnded?.Invoke();
    }
}

namespace Fika.Core.Networking.LiteNetLib
{
    public enum DeliveryMethod
    {
        ReliableOrdered,
    }

    public class NetPeer { }
}

namespace Fika.Core.Networking.LiteNetLib.Utils
{
    public interface INetSerializable
    {
        void Serialize(NetDataWriter w);
        void Deserialize(NetDataReader r);
    }

    public class NetDataWriter
    {
        public Queue<object> Data = new();

        public void Put<T>(T value) => Data.Enqueue(value!);
    }

    public class NetDataReader(Queue<object> data)
    {
        public byte GetByte() => (byte)data.Dequeue();

        public string GetString(int max)
        {
            var s = (string)data.Dequeue();
            if (s.Length > max)
                throw new Exception("overlong");
            return s;
        }

        public long GetLong() => (long)data.Dequeue();

        public int GetInt() => (int)data.Dequeue();

        public float GetFloat() => (float)data.Dequeue();
    }
}

namespace Fika.Core.Networking
{
    public interface IFikaNetworkManager
    {
        void RegisterPacket<T, U>(Action<T, U> callback);
        void UnregisterPacket<T>();
        void SendDataToPeer<T>(ref T data, DeliveryMethod method, NetPeer peer);
        void SendData<T>(ref T data, DeliveryMethod method);
    }

    public class Network : IFikaNetworkManager
    {
        public List<(DoorPacket packet, NetPeer? peer)> Sent = new();
        public Action<DoorPacket, NetPeer>? Receiver;

        public void RegisterPacket<T, U>(Action<T, U> c) =>
            Receiver = (p, n) => c((T)(object)p, (U)(object)n);

        public void UnregisterPacket<T>() => Receiver = null;

        public void SendDataToPeer<T>(ref T p, DeliveryMethod method, NetPeer n) =>
            Sent.Add(((DoorPacket)(object)p!, n));

        public void SendData<T>(ref T p, DeliveryMethod method) =>
            Sent.Add(((DoorPacket)(object)p!, null));
    }

    public class FikaServer : Network { }

    public class FikaClient : Network
    {
        public NetPeer ServerConnection = new();
    }
}

namespace Fika.Core.Modding
{
    public class FikaEvent { }

    public static class FikaEventDispatcher
    {
        public static event Action<FikaEvent>? OnFikaEvent;

        public static void Emit(FikaEvent e) => OnFikaEvent?.Invoke(e);
    }
}

namespace Fika.Core.Modding.Events
{
    using Fika.Core.Modding;
    using Fika.Core.Networking;

    public class FikaNetworkManagerCreatedEvent(IFikaNetworkManager m) : FikaEvent
    {
        public IFikaNetworkManager Manager = m;
    }

    public class FikaNetworkManagerDestroyedEvent(IFikaNetworkManager m) : FikaEvent
    {
        public IFikaNetworkManager Manager = m;
    }

    public class PeerConnectedEvent(IFikaNetworkManager m, NetPeer p) : FikaEvent
    {
        public IFikaNetworkManager NetworkManager = m;
        public NetPeer Peer = p;
    }

    public class PeerDisconnectedEvent(IFikaNetworkManager m, NetPeer p) : FikaEvent
    {
        public IFikaNetworkManager NetworkManager = m;
        public NetPeer Peer = p;
    }
}

namespace EFT
{
    public class Player
    {
        public UnityEngine.Vector3 Position;
        public Health HealthController = new();
        public int Teleports;

        public void Teleport(UnityEngine.Vector3 v)
        {
            Teleports++;
            Position = v;
        }
    }

    public class Health
    {
        public bool IsAlive = true;
    }
}

namespace Fika.Core.Main.Players
{
    public class FikaBot : FikaPlayer
    {
        public Sender BotPacketSender = new();
    }

    public class Sender
    {
        public bool SendState = true,
            isActiveAndEnabled = true;
        public int Snapshots;

        public void SendPlayerState()
        {
            Snapshots++;
        }
    }
}

namespace Comet.Api
{
    public static class MovementRecoveryNetwork
    {
        public static Func<EFT.Player, UnityEngine.Vector3, bool>? Teleport;
    }
}
