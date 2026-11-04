using System;
using System.Collections.Generic;

namespace Comet.Core;

public sealed class OwnGrenadeSafetyConfig
{
    public bool Enabled { get; set; } = true;
    public double DangerMargin { get; set; } = 1;
    public double ExitPadding { get; set; } = 4;
    public double DepartureGrace { get; set; } = .35;
    public double Refresh { get; set; } = .1;
    public void Validate()
    {
        CometConfig.Range(DangerMargin,0,5,nameof(DangerMargin));
        CometConfig.Range(ExitPadding,1,8,nameof(ExitPadding));
        CometConfig.Range(DepartureGrace,0,.35,nameof(DepartureGrace));
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
    public bool Expired(double now)=>UnknownAt>=0&&now-UnknownAt>=Math.Max(8,Fuse+5);
    public bool Grace(double now,OwnGrenadeSafetyConfig c)=>!Exploded&&UnknownAt<0&&Departing&&now>=ThrownAt&&now-ThrownAt<c.DepartureGrace;
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
        return (position-g.Position).Length<g.Radius+(escaping?c.ExitPadding:0)
            &&(escaping||!solidCover(position,g.Position));
    }
    // Check the route, not just the endpoint. An exposed bot may leave monotonically,
    // but may not get closer to this or another known grenade on its escape route.
    public static bool SafeRoute(OwnGrenadeEvidence g,IReadOnlyList<DoorPoint> points,double now,OwnGrenadeSafetyConfig c,Func<DoorPoint,DoorPoint,bool> solidCover,bool own=true)
    {
        if(g.Exploded||g.Expired(now)||own&&g.Grace(now,c)||points.Count<2)return true;
        double last=(points[0]-g.Position).Length;
        bool leaving=last<g.Radius+c.ExitPadding&&!solidCover(points[0],g.Position);
        int budget=128;
        for(int i=1;i<points.Count;i++)
        {
            var a=points[i-1];var d=points[i]-a;
            if(!a.Finite||!d.Finite)return false;
            int samples=Math.Max(1,(int)Math.Ceiling(d.Length/.5));
            // Far-away segments need no physics queries.
            double t=d.Dot(d)>0?Math.Max(0,Math.Min(1,(g.Position-a).Dot(d)/d.Dot(d))):0;
            if((a+d*t-g.Position).Length>g.Radius+c.ExitPadding){leaving=false;last=(points[i]-g.Position).Length;continue;}
            for(int n=1;n<=samples;n++)
            {
                if(--budget<0)return false; // bounded conservative handoff
                var p=a+d*((double)n/samples);double distance=(p-g.Position).Length;
                if(distance<g.Radius+c.ExitPadding)
                {
                    if(leaving&&distance>=last-.01){last=distance;continue;}
                    if(!solidCover(p,g.Position))return false;
                }
                else leaving=false;
                last=distance;
            }
        }
        return true;
    }
}
