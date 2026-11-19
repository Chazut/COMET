using Fika.Core.Networking.LiteNetLib.Utils;

namespace Comet.Fika;

public struct DoorPacket : INetSerializable
{
    public byte Kind;
    public string Session,
        Door;
    public long Revision;
    public int State;
    public float Angle;

    public void Serialize(NetDataWriter w)
    {
        w.Put((byte)1);
        w.Put(Kind);
        w.Put(Session ?? "");
        w.Put(Door ?? "");
        w.Put(Revision);
        w.Put(State);
        w.Put(Angle);
    }

    public void Deserialize(NetDataReader r)
    {
        if (r.GetByte() != 1)
            throw new System.IO.InvalidDataException("COMET door protocol mismatch");
        Kind = r.GetByte();
        Session = r.GetString(64);
        Door = r.GetString(256);
        Revision = r.GetLong();
        State = r.GetInt();
        Angle = r.GetFloat();
    }
}
