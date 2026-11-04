using System;
using System.Collections.Generic;
using System.Linq;

namespace Comet.Core;

public sealed class MedicalChoiceConfig
{
    public bool Enabled { get; set; } = true;
    public double Interval { get; set; } = .5;
    public double HitDelay { get; set; } = 2;
    public double EmergencyStimDelay { get; set; } = .5;
    public double StimHealth { get; set; } = .35;
    public double FootstepAge { get; set; } = 5;
    public double FootstepDistance { get; set; } = 30;
    public double SurgeryQuiet { get; set; } = 25;
    public double SurgeryDistance { get; set; } = 40;
    public double RetryDelay { get; set; } = 3;
    public void Validate()
    {
        CometConfig.Range(Interval, .25, 2, nameof(Interval));
        CometConfig.Range(HitDelay, .5, 10, nameof(HitDelay));
        CometConfig.Range(EmergencyStimDelay, .25, HitDelay, nameof(EmergencyStimDelay));
        CometConfig.Range(StimHealth, .1, .6, nameof(StimHealth));
        CometConfig.Range(FootstepAge, 1, 15, nameof(FootstepAge));
        CometConfig.Range(FootstepDistance, 5, 60, nameof(FootstepDistance));
        CometConfig.Range(SurgeryQuiet, 10, 90, nameof(SurgeryQuiet));
        CometConfig.Range(SurgeryDistance, 20, 100, nameof(SurgeryDistance));
        CometConfig.Range(RetryDelay, 1, 15, nameof(RetryDelay));
    }
}

public sealed class MedicalOption
{
    public string Action { get; set; } = "Wait";
    public string Item { get; set; } = "";
    public string Part { get; set; } = "";
    public string Need { get; set; } = "";
    public bool Usable { get; set; }
    public double Duration { get; set; }
    public double Resource { get; set; }
    public double Score { get; set; }
    public string Reason { get; set; } = "medical-unavailable";
    public MedicalOption Copy() => (MedicalOption)MemberwiseClone();
}
public sealed class MedicalFrame
{
    public double Now, Health = 1, SinceHit = 999, Aggression = .5;
    public bool HeavyBleed, LightBleed, Vital, ReachedCover, ProtectiveCover, Moving, Visible, Footsteps, ThreatsIncomplete;
    public double Nearest = 100000, Quiet = 999;
    public int Contacts;
    public string Contact = "", Priority = "";
    public double? X, Y, Z;
    public readonly List<MedicalOption> Options = new();
}
public sealed class MedicalTrace
{
    public string State { get; set; } = "evaluated";
    public string Choice { get; set; } = "Wait";
    public string Reason { get; set; } = "medical-wait";
    public string Item { get; set; } = "";
    public string Part { get; set; } = "";
    public string Need { get; set; } = "";
    public double Duration { get; set; }
    public double Health { get; set; }
    public bool HeavyBleed { get; set; }
    public bool LightBleed { get; set; }
    public bool Vital { get; set; }
    public bool Cover { get; set; }
    public bool Visible { get; set; }
    public bool Footsteps { get; set; }
    public bool ThreatsIncomplete { get; set; }
    public int Contacts { get; set; }
    public string Contact { get; set; } = "";
    public double? X { get; set; }
    public double? Y { get; set; }
    public double? Z { get; set; }
    public double Quiet { get; set; }
    public double Distance { get; set; }
    public double SinceHit { get; set; }
    public bool Started { get; set; }
    public bool Applied { get; set; }
    public double ValidFor { get; set; }
    public string Outcome { get; set; } = "unconfirmed";
    public MedicalOption[] Alternatives { get; set; } = Array.Empty<MedicalOption>();
    public MedicalTrace Copy() => (MedicalTrace)MemberwiseClone();
}

// Start selection only. Execution, resources and actual duration belong to native medicine.
public sealed class MedicalChoice
{
    public MedicalTrace? Trace { get; private set; }
    public bool Active => Trace?.State is "selected" or "requested" or "started";
    public double SelectedAt { get; private set; }
    public double RequestedAt { get; private set; }
    public double StartedAt { get; private set; }
    public double RetryAt { get; private set; }
    public MedicalTrace Evaluate(MedicalFrame f, MedicalChoiceConfig c)
    {
        if (Active) return Trace!;
        var t = new MedicalTrace { Health=f.Health,HeavyBleed=f.HeavyBleed,LightBleed=f.LightBleed,Vital=f.Vital,
            Cover=f.ReachedCover&&f.ProtectiveCover,Visible=f.Visible,Footsteps=f.Footsteps,Contacts=f.Contacts,
            Contact=f.Contact,X=f.X,Y=f.Y,Z=f.Z,Quiet=f.Quiet,Distance=f.Nearest,SinceHit=f.SinceHit,ThreatsIncomplete=f.ThreatsIncomplete };
        var options=f.Options.Select(o=>o.Copy()).ToList();
        foreach (var o in options)
        {
            o.Score=-1;
            if (!o.Usable || !Finite(o.Duration) || o.Duration<=0 || o.Duration>120) { o.Reason="medical-no-usable-treatment";continue; }
            string blocked=f.Priority;
            if (f.Now<RetryAt) blocked="medical-retry-delay";
            bool emergency=o.Need is "heavy-bleed" or "vital";
            bool unsafeNow=f.Visible||f.Footsteps||f.ThreatsIncomplete;
            if(blocked.Length==0)
            {
                if(o.Action=="Surgery")
                {
                    if(f.HeavyBleed||f.LightBleed||f.Vital)blocked="medical-urgent-injury-first";
                    else if(f.Moving||!t.Cover&&f.Contacts>0)blocked="medical-cover-not-reached";
                    else if(unsafeNow||f.Contacts>0&&(f.Quiet<c.SurgeryQuiet||f.Nearest<=c.SurgeryDistance))blocked="medical-surgery-threat";
                    else if(f.SinceHit<c.HitDelay)blocked="medical-recent-hit";
                    else o.Score=45+30*(1-f.Aggression);
                }
                else if(o.Action=="Stims")
                {
                    if(f.Health>c.StimHealth&&!f.Vital)blocked="medical-stim-not-needed";
                    else if(f.SinceHit<c.EmergencyStimDelay)blocked="medical-recent-hit";
                    else if(f.Moving)blocked="medical-still-moving";
                    else o.Score=85+(f.Vital?20:0);
                }
                else if(o.Action=="FirstAid")
                {
                    if(f.SinceHit<(emergency?c.EmergencyStimDelay:c.HitDelay))blocked="medical-recent-hit";
                    else if(f.Moving)blocked="medical-still-moving";
                    else if(!emergency&&(unsafeNow||f.Contacts>0&&!t.Cover&&f.Quiet<10))blocked="medical-exposed";
                    else o.Score=(emergency?120:o.Need=="light-bleed"?90:65)+10*(1-f.Health)-Math.Min(10,o.Duration*.25);
                }
            }
            o.Reason=blocked.Length>0?blocked:o.Score>=0?"medical-"+o.Need:"medical-unsupported";
            if(blocked.Length>0)o.Score=-1;
        }
        options.Add(new MedicalOption{Action="Wait",Usable=true,Score=30,Reason=f.Priority.Length>0?f.Priority:f.Now<RetryAt?"medical-retry-delay":"medical-wait-for-safety-or-supplies"});
        t.Alternatives=options.OrderByDescending(o=>o.Score).ToArray();var best=t.Alternatives[0];
        t.Choice=best.Action;t.Reason=best.Reason;t.Item=best.Item;t.Part=best.Part;t.Need=best.Need;t.Duration=best.Duration;
        if(best.Action!="Wait"){t.State="selected";SelectedAt=f.Now;}
        return Trace=t;
    }
    public void Request(double now){if(Trace?.State!="selected")return;Trace.State="requested";Trace.Reason="medical-native-request";RequestedAt=now;}
    public void ObserveStart(double now){if(Trace?.State!="requested")return;Trace.State="started";Trace.Reason="medical-hands-observed";Trace.Started=true;Trace.Applied=true;Trace.ValidFor=1;StartedAt=now;}
    public void End(double now,string reason,MedicalChoiceConfig c,string outcome="unconfirmed")
    {if(!Active)return;Trace!.State="ended";Trace.Reason=reason;Trace.Applied=false;Trace.ValidFor=0;Trace.Outcome=outcome;RetryAt=now+c.RetryDelay;}
    public static bool Finite(double value)=>!double.IsNaN(value)&&!double.IsInfinity(value);
}
