using System;
namespace Comet.Core;

// Stable phases keep periodic bot maintenance spread across frames. Late frames
// run once, never a catch-up burst. Tactical callbacks still validate immediately.
public sealed class PeriodicWorkSlot
{
    private readonly double interval,phase;
    private double next=double.NaN,last=double.NaN;
    public PeriodicWorkSlot(int ordinal,double interval=.25)
    {this.interval=interval;phase=((ordinal%17+17)%17)*interval/17;}
    public bool Due(double now)
    {
        if(!DoorPolicy.Finite(now)||now<0)return false;
        if(double.IsNaN(next)||now<last)
        {next=Math.Floor(now/interval)*interval+phase;if(next<now)next+=interval;}
        last=now;
        if(now<next)return false;
        next+=(Math.Floor((now-next)/interval)+1)*interval;
        return true;
    }
}
