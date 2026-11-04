using System;

namespace Comet.Core;

public sealed class DoorSearchClearConfig
{
    public bool Enabled { get; set; } = true;
    public double Chance { get; set; } = 70;
    public double ContactLifetime { get; set; } = 25;
    public bool AllowFeint { get; set; } = true;
    public void Validate(double recentLimit)
    {
        CometConfig.Range(Chance,0,100,nameof(Chance));
        CometConfig.Range(ContactLifetime,recentLimit+.1,120,nameof(ContactLifetime));
    }
}

// This is a chance to prepare an already selected search, never evidence of an enemy still being present.
public static class DoorSearchClear
{
    public static double Limit(DoorSearchClearConfig c,double memoryLimit,double approachLimit)
        =>Math.Min(c.ContactLifetime,Math.Min(memoryLimit,approachLimit));
    public static double Factor(double age,double recentLimit,double fadeUntil)
        =>!DoorRoomClear.Valid(age)||!DoorRoomClear.Valid(recentLimit)||!DoorRoomClear.Valid(fadeUntil)||fadeUntil<=recentLimit?0:
            Math.Max(0,Math.Min(1,(fadeUntil-age)/(fadeUntil-recentLimit)));
    public static string Gate(double age,DoorRoomClearConfig recent,DoorSearchClearConfig c,double memoryLimit,double approachLimit)
    {
        if(!recent.Enabled||!c.Enabled)return "search-clear-disabled";
        if(!DoorRoomClear.Valid(age)||!DoorRoomClear.Valid(memoryLimit)||!DoorRoomClear.Valid(approachLimit))return "search-clear-invalid-age";
        if(age<=recent.ContactLifetime)return "search-clear-recent-contact-d07-priority";
        if(age>=Limit(c,memoryLimit,approachLimit))return "search-clear-contact-expired";
        if(c.Chance<=0)return "search-clear-chance-disabled";
        return "";
    }
}
