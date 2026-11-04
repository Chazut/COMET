using System;

namespace Comet.Core;

public sealed class DoorFakeMedicalConfig
{
    public bool Enabled { get; set; }=true;
    public bool Stimulators { get; set; }=true;
    public double Preference { get; set; }=50;
    public void Validate()=>CometConfig.Range(Preference,0,100,nameof(Preference));
}

public static class DoorFeintChoice
{
    public static string Pick(bool grenade,string medical,double roll,double preference)
    {
        if(!DoorPolicy.Finite(roll)||roll<0||roll>=1||!DoorPolicy.Finite(preference)||preference<0||preference>100)return "";
        bool med=medical=="heal"||medical=="stim";
        if(!grenade&&!med)return "";
        return med&&(!grenade||roll*100<preference)?medical:"grenade";
    }
}

// Evidence is observational. Never refunds an item, reverses healing or asserts who heard the sound.
public sealed class DoorMedicalTrace
{
    public string Kind { get; set; }="";
    public string Item { get; set; }="";
    public string BodyPart { get; set; }="";
    public double? ResourceBefore { get; set; }
    public double? ResourceAfter { get; set; }
    public double ResourceSpent { get; set; }
    public bool ConsumptionObserved { get; set; }
    public bool UseCallbackCompleted { get; set; }
    public bool UseCallbackSucceeded { get; set; }
    public bool NativeStopRequested { get; set; }
    public bool MedicalHandoff { get; set; }
    public bool CooldownRestored { get; set; }
    public string ConsumptionEvidence { get; set; }="not-observed";
    public void Sample(double? resource,bool useCompleted,bool useSucceeded,bool itemRemoved)
    {
        if(resource.HasValue&&DoorPolicy.Finite(resource.Value)&&resource.Value>=0)ResourceAfter=resource;
        UseCallbackCompleted|=useCompleted;UseCallbackSucceeded|=useSucceeded;
        if(ResourceBefore.HasValue&&ResourceAfter.HasValue&&ResourceAfter.Value<ResourceBefore.Value-.001)
        {ResourceSpent=Math.Max(ResourceSpent,ResourceBefore.Value-ResourceAfter.Value);ConsumptionObserved=true;ConsumptionEvidence="resource-decreased";}
        // MedsController invokes a successful callback even when cancelled or unable to apply.
        else if(itemRemoved){ConsumptionObserved=true;ConsumptionEvidence="native-medical-item-consumed";}
    }
    // Restore only our StopUse side effect, only if it still equals the native value just written.
    public static double? RestoreCooldown(double before,double after,double now,double nativeDelay,bool owned,bool urgent,bool consumed,bool callback)
    {
        if(!owned||urgent||consumed||callback||!DoorPolicy.Finite(before)||!DoorPolicy.Finite(after)||!DoorPolicy.Finite(now)||!DoorPolicy.Finite(nativeDelay)
            ||nativeDelay<0||Math.Abs(after-(now+nativeDelay))>.02)return null;
        return Math.Min(after,Math.Max(before,now+.5));
    }
}
