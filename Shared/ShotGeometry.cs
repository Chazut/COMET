using System;
using System.Collections.Generic;

namespace Comet.Core;

public sealed class ShotGeometryConfig
{
    public bool Enabled { get; set; } = true;
    public double CacheSeconds { get; set; } = .15;
    public double MuzzleMovement { get; set; } = .25;
    public double DirectionDegrees { get; set; } = 5;
    public bool AllowPenetration { get; set; } = true;
    public void Validate()
    {
        CometConfig.Range(CacheSeconds,0,.3,nameof(CacheSeconds));
        CometConfig.Range(MuzzleMovement,.02,.5,nameof(MuzzleMovement));
        CometConfig.Range(DirectionDegrees,.5,10,nameof(DirectionDegrees));
    }
}
// Detached native surface/ammunition observations. No random draw, Shot or Unity reference.
public sealed class ShotSurface
{
    public string Material="unknown",Lane="aim",Kind="solid";
    public double Distance,Level,Chance,Ricochet,Incidence;
    public bool Known,Forward=true;
}
public sealed class ShotGeometryFrame
{
    public bool Complete,OriginClear,AllySafe,AmmoKnown;
    public double Power,Deviation,AmmoChance,AmmoRicochet,Degradation;
    public ShotSurface[] Surfaces=Array.Empty<ShotSurface>();
}
public sealed class ShotGeometryTrace
{
    public string State="deferred",Reason="geometry-budget",Consumer="",Target="",Action="",Weapon="",Ammo="";
    public bool Allowed,Fresh,AllySafe,Cached,PenetrationEstimated;
    public int PassThroughSurfaces;
    public double Age,ValidFor=.2,EstimatedChance,ConservativePower,MuzzleX,MuzzleY,MuzzleZ,AimX,AimY,AimZ;
    public ShotSurface[] Surfaces=Array.Empty<ShotSurface>();
    public string[] Alternatives={"Wait for a clear muzzle or opening","Keep the native movement or cover action","Normal SAIN fire on a visible target"};
}
public static class ShotGeometryPolicy
{
    public static ShotGeometryTrace Evaluate(ShotGeometryFrame f,ShotGeometryConfig c)
    {
        var t=new ShotGeometryTrace{Fresh=f.Complete,AllySafe=f.AllySafe};
        if(!f.Complete)return t;
        t.State="blocked";
        if(!f.OriginClear){t.Reason="geometry-muzzle-inside";return t;}
        if(!f.AllySafe){t.Reason="geometry-ally-in-line";return t;}
        if(f.Surfaces==null||f.Surfaces.Length>32){t.Reason="geometry-incomplete-surfaces";return t;}
        t.Surfaces=Snapshot(f.Surfaces);
        if(f.Surfaces.Length==0){t.State="clear";t.Reason="geometry-clear";t.Allowed=true;return t;}
        // Native power is normally distributed, then degraded by speed. Use mean - 2 deviations
        // and the configured native degradation floor: conservative estimate, never guaranteed penetration.
        double initial=Finite(f.Power)&&Finite(f.Deviation)?Math.Max(0,f.Power-2*f.Deviation):0,chance=1,minPower=initial;bool estimated=false;
        var powers=new Dictionary<string,double>();var chances=new Dictionary<string,double>();
        var distances=new Dictionary<string,double>();
        foreach(var s in f.Surfaces)
        {
            if(s==null||s.Lane is not ("aim" or "barrel")||s.Kind is not ("solid" or "door" or "glass")||!Finite(s.Distance)||s.Distance<0)
            {t.Reason="geometry-surface-unknown";return t;}
            if(distances.TryGetValue(s.Lane,out var previous)&&s.Distance<previous){t.Reason="geometry-incomplete-surfaces";return t;}
            distances[s.Lane]=s.Distance;
            // User-approved policy: doors and window glass never veto a shot. This is permission,
            // not a claim that the native bullet will penetrate. Other surfaces and allies still veto.
            if(s.Kind is "door" or "glass")
            {
                t.PassThroughSurfaces++;
                // Permission ignores this surface's resistance, but later solids still use a degraded power estimate.
                if(s.Forward&&Unit(f.Degradation))powers[s.Lane]=(powers.TryGetValue(s.Lane,out var priorPower)?priorPower:initial)*f.Degradation;
                continue;
            }
            if(!c.AllowPenetration){t.Reason="geometry-penetration-disabled";return t;}
            if(!s.Known||!Finite(s.Level)||s.Level<0||!Unit(s.Chance)||!Unit(s.Ricochet)||!Finite(s.Incidence)||s.Incidence<0||s.Incidence>180)
            {t.Reason="geometry-surface-unknown";return t;}
            if(!f.AmmoKnown||!Finite(f.Power)||f.Power<=0||!Finite(f.Deviation)||f.Deviation<0
                ||!Unit(f.AmmoChance)||!Unit(f.AmmoRicochet)||!Unit(f.Degradation)||f.Degradation<=0)
            {t.Reason="geometry-ammo-unknown";return t;}
            estimated=true;
            double power=powers.TryGetValue(s.Lane,out var p)?p:initial;
            if(s.Forward)power*=f.Degradation;
            powers[s.Lane]=power;minPower=Math.Min(minPower,power);t.ConservativePower=minPower;
            // Matches the inspected native standard collider predicates, without invoking RNG or hit effects.
            if(s.Chance<=0||power<=s.Level){t.Reason="geometry-solid-obstacle";return t;}
            if(s.Ricochet>0&&f.AmmoRicochet>0&&s.Incidence>42.5&&s.Incidence<80)
            {t.Reason="geometry-ricochet-risk";return t;}
            double laneChance=(chances.TryGetValue(s.Lane,out var q)?q:1)*Math.Min(1,f.AmmoChance+s.Chance);
            chances[s.Lane]=laneChance;chance=Math.Min(chance,laneChance);t.EstimatedChance=chance;
            if(chance<.5){t.Reason="geometry-low-penetration-chance";return t;}
        }
        t.State=estimated?"penetrable":"pass-through";t.Reason=estimated?"geometry-native-penetration-plausible":"geometry-door-or-glass";t.Allowed=true;t.PenetrationEstimated=estimated;return t;
    }
    private static ShotSurface[] Snapshot(ShotSurface[] surfaces)
    {
        var result=new ShotSurface[surfaces.Length];
        for(int i=0;i<result.Length;i++)
        {
            var s=surfaces[i]??new();
            result[i]=new(){Kind=s.Kind??"solid",Material=s.Material??"unknown",Lane=s.Lane??"aim",Known=s.Known,Forward=s.Forward,
                Distance=Finite(s.Distance)?Math.Max(0,s.Distance):0,Level=Finite(s.Level)?Math.Max(0,s.Level):0,
                Chance=Unit(s.Chance)?s.Chance:0,Ricochet=Unit(s.Ricochet)?s.Ricochet:0,Incidence=Finite(s.Incidence)?Math.Max(0,Math.Min(180,s.Incidence)):0};
        }
        return result;
    }
    private static bool Finite(double x)=>DoorPolicy.Finite(x);
    private static bool Unit(double x)=>Finite(x)&&x>=0&&x<=1;
    public static bool CanReuse(double now,double at,int frame,int checkedFrame,ShotGeometryTrace? trace,
        DoorPoint muzzle,DoorPoint oldMuzzle,DoorPoint aim,DoorPoint oldAim,DoorPoint direction,DoorPoint oldDirection,
        string key,string oldKey,bool stable,ShotGeometryConfig c)
    {
        if(trace==null||!trace.Fresh||trace.State=="released"||trace.ValidFor<=0||!Finite(now)||!Finite(at)||now<at||now-at>c.CacheSeconds||key!=oldKey
            ||!muzzle.Finite||!aim.Finite||!direction.Finite||!oldMuzzle.Finite||!oldAim.Finite||!oldDirection.Finite
            ||(muzzle-oldMuzzle).Length>=c.MuzzleMovement||(aim-oldAim).Length>=c.MuzzleMovement)return false;
        double d=direction.Length*oldDirection.Length;if(d<.001)return false;
        double degrees=Math.Acos(Math.Max(-1,Math.Min(1,direction.Dot(oldDirection)/d)))*180/Math.PI;
        if(degrees>=c.DirectionDegrees)return false;
        // Positive results and allies are live safety decisions: never reuse across frames.
        // A static blocked surface may be reused briefly, but not an ally or origin-overlap refusal.
        return frame==checkedFrame||(stable&&!trace.Allowed&&trace.Reason is not ("geometry-ally-in-line" or "geometry-muzzle-inside"));
    }
}
