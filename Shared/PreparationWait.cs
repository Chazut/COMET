namespace Comet.Core;

// Waiting for the shared probe budget is not a failed geometric attempt.
// Credit is capped per preparation, never per callback, and never extends live movement.
public sealed class PreparationWait
{
    public double Seconds { get; private set; }
    private double last=-1;
    public void Defer(double now,double delta)
    {
        if(!DoorPolicy.Finite(now)||!DoorPolicy.Finite(delta)||now<=last||delta<=0)return;
        last=now;Seconds=System.Math.Min(2,Seconds+System.Math.Min(.1,delta));
    }
    public void Clear(){Seconds=0;last=-1;}
}
