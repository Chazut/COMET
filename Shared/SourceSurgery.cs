using System;
namespace Comet.Core;

// BotSurgery.CheckEnemies + CheckRelaxed from fbb86b83. No COMET cancellation radius.
public sealed class SourceSurgery
{
    private bool relaxed,rollHit;
    private double rollUntil;
    public static (double chance,double quiet,double distance) Limits(string p)=>p switch {
        "Rat"=>(80,20,30),"Coward"=>(80,20,35),"SnappingTurtle"=>(70,20,30),"Timmy"=>(60,25,40),
        "Chad"=>(35,30,45),"GigaChad"=>(25,30,50),"Wreckless"=>(15,35,50),_=>(50,25,40)};
    public bool Allows(double now,string personality,bool usingKit,bool usable,bool strictClear,bool inCover,
        bool visible,double nearest,double seenAge,double knownAge,double sinceHit,Func<double> roll)
    {
        if(!usable)return false;
        if(strictClear)return true;
        var c=Limits(personality);
        if(usingKit&&relaxed){bool keep=sinceHit>=1&&!visible&&nearest>=c.distance*.5;if(!keep)relaxed=false;return keep;}
        relaxed=false;
        if(!inCover||visible||nearest<c.distance||seenAge<c.quiet||knownAge<c.quiet)return false;
        if(now>=rollUntil){rollUntil=now+20;rollHit=roll()<c.chance;}
        if(!rollHit)return false;
        relaxed=true;return true;
    }
}
