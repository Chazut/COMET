using System;

namespace Comet.Core;

// Read-only locomotion evidence. It does not attribute posture to the tactical
// planner, measure footstep volume, or prove that an opponent did not hear it.
public sealed class MovementObservation
{
    public double Pose { get; set; }
    public double Speed { get; set; }
    public bool Prone { get; set; }
    public bool Sprinting { get; set; }
    public double ValidFor { get; set; }=1.5;
}

public sealed class MovementObserver
{
    private double last=-1;
    public bool Due(double now,bool combat)
    {
        if(!combat||!Finite(now)||now<0)return false;
        if(last>=0&&now>=last&&now-last<1)return false;
        last=now;return true;
    }
    public static MovementObservation? Capture(double pose,double vx,double vz,bool prone,bool sprinting)
    {
        if(!Finite(pose)||pose<0||pose>1||!Finite(vx)||!Finite(vz))return null;
        double speed=Math.Sqrt(vx*vx+vz*vz);
        // A teleport or invalid native velocity is not evidence of locomotion.
        if(!Finite(speed)||speed>20)return null;
        return new(){Pose=Math.Round(pose,3),Speed=Math.Round(speed,2),Prone=prone,Sprinting=sprinting};
    }
    private static bool Finite(double x)=>!double.IsNaN(x)&&!double.IsInfinity(x);
}
