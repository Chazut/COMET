using System;

namespace Comet.Core;

// D10 selects a use of an already accepted Hold. It never creates a new combat decision.
public sealed class DoorHoldTrace
{
    public string Outcome { get; set; }="pending";
    public string Reason { get; set; }="hold-door-comparing";
    public string Door { get; set; }="";
    public double CurrentProtection { get; set; }
    public double CandidateProtection { get; set; }
    public bool CurrentAngle { get; set; }
    public double PathLength { get; set; }
    public double EstimatedTravel { get; set; }
    public double AvailableTime { get; set; }
    public double UsefulWait { get; set; }
    public double MinimumUsefulWait { get; set; }
    public double ValidFor { get; set; }=1.5;
    public string[] Alternatives { get; set; }=Array.Empty<string>();
}

public static class DoorHoldSelection
{
    // A conservative walking estimate, not observed travel time or a guaranteed arrival.
    public const double EstimatedWalkingSpeed=1.2;
    public const double SettleAllowance=.5;
    public static bool Protected(double score)=>Finite(score)&&score>=2d/3&&score<=1;
    public static double Rank(double protection,double distance)=>100*(1-protection)+distance;
    public static DoorHoldTrace Evaluate(bool currentAngle,double currentProtection,bool candidateSafe,double candidateProtection,
        double pathLength,double available,double waitDuration,double minimum,double approachLimit)
    {
        var t=new DoorHoldTrace{CurrentAngle=currentAngle,CurrentProtection=Safe(currentProtection),CandidateProtection=Safe(candidateProtection),
            PathLength=Safe(pathLength),AvailableTime=Safe(available),MinimumUsefulWait=Safe(minimum),
            Alternatives=new[]{"Keep the current protected angle","Move only for a safe doorway angle with enough time","Keep a useful native Hold or reconsider another action"}};
        if(!Fraction(currentProtection)||!Fraction(candidateProtection)||!Finite(pathLength)||pathLength<0||!Finite(available)||available<=0
            ||!Finite(waitDuration)||waitDuration<=0||!Finite(minimum)||minimum<0||!Finite(approachLimit)||approachLimit<=0)
        {t.Outcome="reconsider";t.Reason="hold-door-invalid-or-expired";return t;}
        if(currentAngle&&Protected(currentProtection))
        {t.Outcome="stay";t.Reason="hold-door-current-angle-good";t.UsefulWait=Math.Min(available,waitDuration);return t;}
        t.EstimatedTravel=pathLength/EstimatedWalkingSpeed+SettleAllowance;
        t.UsefulWait=Math.Max(0,Math.Min(waitDuration,available-t.EstimatedTravel));
        string refusal=!candidateSafe?"hold-door-no-safe-angle":!Protected(candidateProtection)?"hold-door-insufficient-protection":
            candidateProtection+1e-6<currentProtection?"hold-door-worse-protection":
            t.EstimatedTravel>approachLimit||t.UsefulWait+1e-6<minimum||t.UsefulWait<=0?"hold-door-not-enough-time":"";
        if(refusal.Length>0){t.Outcome=Protected(currentProtection)?"native-hold":"reconsider";t.Reason=refusal;}
        else {t.Outcome="move";t.Reason="hold-door-useful-reposition";}
        return t;
    }
    private static double Safe(double x)=>Finite(x)&&x>=0?x:0;
    private static bool Fraction(double x)=>Finite(x)&&x>=0&&x<=1;
    private static bool Finite(double x)=>DoorPolicy.Finite(x);
}
