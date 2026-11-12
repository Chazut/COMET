// Adapted from R_F's zzap FearModel.cs, fbb86b838e0fa692b759b2c0b11fc30158a53876.
using System.Collections.Generic;
namespace Comet.Core;
public sealed class FearRaidMemory
{
    private readonly List<(int Group, DoorPoint Position, double Time)> deaths = new();
    private readonly List<double> kills = new();
    public void Death(int group, DoorPoint position, double now)
    {
        deaths.Add((group, position, now));
        if (deaths.Count > 64) deaths.RemoveAt(0);
    }
    public void PlayerKill(double now)
    {
        kills.Add(now);
        if (kills.Count > 32) kills.RemoveAt(0);
    }
    public double? NearbyDeath(int group, DoorPoint position, double now)
    {
        foreach (var d in deaths)
        {
            double x=d.Position.X-position.X,y=d.Position.Y-position.Y,z=d.Position.Z-position.Z;
            if(d.Group==group && now-d.Time<30 && x*x+y*y+z*z<1600) return d.Time;
        }
        return null;
    }
    public double Streak(bool humanEnemy, double now)
    {
        if (!humanEnemy) return 0;
        int count=0;foreach(var k in kills)if(now-k<300)count++;
        return System.Math.Min(.25,count*.05);
    }
    public void Clear(){deaths.Clear();kills.Clear();}
}
