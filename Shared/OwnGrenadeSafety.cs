using System;
using System.Collections.Generic;

namespace Comet.Core;

public sealed class OwnGrenadeSafetyConfig
{
    public bool Enabled { get; set; } = true;
    public double DangerMargin { get; set; } = 1;
    public double ExitPadding { get; set; } = 4;
    public double DepartureGrace { get; set; } = .7;
    public double Refresh { get; set; } = .1;
    public void Validate()
    {
        CometConfig.Range(DangerMargin,0,5,nameof(DangerMargin));
        CometConfig.Range(ExitPadding,1,8,nameof(ExitPadding));
        CometConfig.Range(DepartureGrace,0,2,nameof(DepartureGrace));
        CometConfig.Range(Refresh,.05,.2,nameof(Refresh));
    }
}

// Scalar evidence survives a missing Unity object, but never turns its disappearance into a blast.
public sealed class OwnGrenadeEvidence
{
    public string Owner="",Item="";
    public int Projectile;
    public DoorPoint Position,Origin;
    public double ThrownAt,SampledAt,Fuse,Radius,UnknownAt=-1;
    public bool Exploded,Departing;
    public string Status => Exploded?"confirmed-explosion":UnknownAt>=0?"outcome-unknown":"live";
    public void Sample(DoorPoint point, double now, DoorPoint ownerPosition)
    {
        // Rebounds and stalled projectiles do not receive the departure grace.
        Departing=UnknownAt<0&&now>SampledAt&&(point-ownerPosition).Length>(Position-ownerPosition).Length+.02;
        Position=point;SampledAt=now;UnknownAt=-1;
    }
    public void Missing(double now){Departing=false;if(UnknownAt<0)UnknownAt=now;}
    public bool Confirm(string owner,int projectile)
    {if(owner!=Owner||projectile!=Projectile)return false;Exploded=true;return true;}
    public bool Expired(double now)=>UnknownAt>=0||now-ThrownAt>8;
    public bool Grace(double now,OwnGrenadeSafetyConfig c)=>!Exploded&&UnknownAt<0&&now>=ThrownAt&&now-ThrownAt<c.DepartureGrace;
}

public sealed class OwnGrenadeSafetyTrace
{
    public string State="tracking",Reason="own-grenade-live",Owner="",Item="",Source="own-throw",Outcome="live";
    public int Projectile,Active;
    public double X,Y,Z,Radius,Distance,Age,ValidFor=.5;
    public bool RouteBlocked,Escape;
    public string[] Alternatives=Array.Empty<string>();
}

public static class OwnGrenadeSafetyRules
{
    public static bool Threat(OwnGrenadeEvidence g,DoorPoint position,bool escaping,double now,OwnGrenadeSafetyConfig c,Func<DoorPoint,DoorPoint,bool> solidCover,bool own=true)
    {
        if(g.Exploded||g.Expired(now)||own&&g.Grace(now,c))return false;
        return (position-g.Position).Length<=6+(escaping?c.ExitPadding:0)
            &&(escaping||!solidCover(position,g.Position));
    }
}
