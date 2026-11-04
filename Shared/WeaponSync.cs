using System;
using System.Linq;

namespace Comet.Core;

public sealed class WeaponSyncConfig
{
    public bool Enabled { get; set; } = true;
    public double BackupSeconds { get; set; } = 5;
    public void Validate()=>CometConfig.Range(BackupSeconds,1,30,nameof(BackupSeconds));
}

// Deadlines use the first event in a burst: sustained inventory events cannot postpone a repair forever.
public sealed class WeaponSyncSchedule
{
    public bool Dirty { get; private set; } = true;
    public string Trigger { get; private set; } = "initial-check";
    public int Revision { get; private set; }
    private double due,backup,last=-1;
    public void Mark(double now,string trigger)
    {
        if(!DoorPolicy.Finite(now)||now<0)return;
        if(!Dirty)due=now+.25;
        Dirty=true;Trigger=trigger;Revision++;
    }
    public bool Due(double now)
    {
        if(!DoorPolicy.Finite(now)||now<0)return false;
        if(last>=0&&now<last)Reset();
        last=now;
        if(!Dirty&&now>=backup){Dirty=true;due=now;Trigger="backup-check";}
        return Dirty&&now>=due;
    }
    public void Defer(double now)=>due=now+.25;
    public void Complete(double now,double interval,int revision)
    {if(revision!=Revision){due=now+.25;return;}Dirty=false;backup=now+interval;}
    public void Reset(){Dirty=true;Trigger="scope-resumed";due=backup=0;last=-1;Revision++;}
}

public sealed class WeaponSyncSlot
{
    public string Slot="",Actual="",Name="",NativeBefore="",SainBefore="",NativeAfter="",SainAfter="";
    public bool NativePresent,SainPresent,NativeMatches,SainMatches,SelectorMatches,ContentsChanged;
    public string NativeOperation="keep",SainOperation="keep";
}
public sealed class WeaponSyncFrame
{
    public bool Safe,NativeCurrentMatches,SainCurrentMatches,ResourcesChanged;
    public string Block="",Held="",NativeCurrent="",SainCurrent="";
    public WeaponSyncSlot[] Slots=Array.Empty<WeaponSyncSlot>();
}
public sealed class WeaponSyncTrace
{
    public string State="deferred",Reason="",Trigger="",Action="",Held="",NativeCurrentBefore="",SainCurrentBefore="",NativeCurrentAfter="",SainCurrentAfter="";
    public bool Changed,Verified,SelectorUpdated,NativeCurrentUpdated,SainCurrentUpdated,ResourcesInvalidated;
    public double ValidFor=2,SampleMilliseconds;
    public WeaponSyncSlot[] Slots=Array.Empty<WeaponSyncSlot>();
}
public static class WeaponSyncPlan
{
    public static bool Prepare(WeaponSyncFrame f)
    {
        if(f.Slots.Length!=3||f.Slots.Select(x=>x.Slot).Distinct().Count()!=3
            ||f.Slots.Any(x=>x.Slot is not ("FirstPrimaryWeapon" or "SecondPrimaryWeapon" or "Holster")))return false;
        if(f.Slots.Where(x=>x.Actual.Length>0).Select(x=>x.Actual).Distinct().Count()!=f.Slots.Count(x=>x.Actual.Length>0))return false;
        foreach(var s in f.Slots)
        {
            s.NativeOperation=s.Actual.Length==0?(s.NativePresent?"remove":"keep"):!s.NativePresent?"add":!s.NativeMatches?"replace":"keep";
            s.SainOperation=s.Actual.Length==0?(s.SainPresent?"remove":"keep"):!s.SainPresent?"add":!s.SainMatches?"replace":s.ContentsChanged?"refresh":"keep";
        }
        return f.Safe;
    }
    public static bool NeedsWork(WeaponSyncFrame f)=>f.ResourcesChanged||!f.NativeCurrentMatches||!f.SainCurrentMatches
        ||f.Slots.Any(s=>s.NativeOperation!="keep"||s.SainOperation!="keep"||!s.SelectorMatches);
    public static bool Matches(WeaponSyncFrame f)=>f.Safe&&f.NativeCurrentMatches&&f.SainCurrentMatches
        &&f.Slots.Length==3&&f.Slots.All(s=>s.NativeMatches&&s.SainMatches&&s.SelectorMatches);
}
