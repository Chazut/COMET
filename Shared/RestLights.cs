using System;

namespace Comet.Core;

public sealed class RestLightsConfig
{
    public bool Enabled { get; set; } = true;
    public double HoldDelay { get; set; } = 2;
    public double MaxSpeed { get; set; } = .5;
    public double EpisodeInterval { get; set; } = 10;
    public void Validate()
    {
        CometConfig.Range(HoldDelay,.5,15,nameof(HoldDelay));
        CometConfig.Range(MaxSpeed,0,1,nameof(MaxSpeed));
        CometConfig.Range(EpisodeInterval,1,120,nameof(EpisodeInterval));
    }
}

// Episode ownership is separate from light state. A rejected command, external ON or mode change
// consumes this pause too; no repeated OFF command until a genuinely new pause/weapon/owner.
public sealed class RestLightsPolicy
{
    public double Since=-1, NextEpisode, Last=-1;
    public string Context="";
    public bool Consumed;
    public int Episode;
    public void Release() { Since=-1;Context="";Consumed=false; }
    public bool Update(double now,string context,bool eligible,RestLightsConfig c)
    {
        if(double.IsNaN(now)||double.IsInfinity(now)||now<0){Release();return false;}
        if(Last>now){Release();NextEpisode=0;}
        Last=now;
        if(!eligible){Release();return false;}
        if(Context!=context){Release();Context=context;Since=now;Episode++;}
        return !Consumed&&now-Since>=c.HoldDelay&&now>=NextEpisode;
    }
    public void Consume(double now,RestLightsConfig c) { Consumed=true;NextEpisode=now+c.EpisodeInterval; }
}

public static class RestLightsOwner
{
    // Only documented, actually active owners. Never infer rest from an old SAIN decision or speed alone.
    public static string Behavior(string layer,string native,string status,string category)
    {
        if(layer=="OrbitBrainLayer")
        {
            if(status!="Finished"||string.IsNullOrEmpty(category)||category=="Exfil"||category=="AirdropLoot"
                ||category.StartsWith("MultiStep:",StringComparison.Ordinal))return "";
            if(category.StartsWith("Ambush",StringComparison.Ordinal))return "Orbit:Camping"+category;
            return category=="SquadCover"?"Orbit:SquadCover":"Orbit:Guarding";
        }
        if(layer=="PatrolAssault"&&(native=="peaceful"||native=="peaceLook"||native=="holdPosition"))return native;
        return "";
    }
}

public sealed class RestLightAccessory
{
    public string Id="",Name="";
    public int Mode;
    public bool Active;
}
public sealed class RestLightsTrace
{
    public string State="waiting",Reason="rest-stabilizing",Owner="",Behavior="",Layer="",Weapon="",Execution="No command";
    public int Episode,Devices,ActiveDevices;
    public double Speed,StillFor,WaitRemaining,CooldownRemaining,ValidFor;
    public bool Requested,Accepted,ObservedOff;
    public RestLightAccessory[] Accessories=Array.Empty<RestLightAccessory>();
    public string[] Alternatives={"Keep the current light state", "Wait for a stable pause and ready hands", "Yield to combat or another light owner"};
}
