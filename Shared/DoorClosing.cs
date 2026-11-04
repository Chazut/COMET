using System;
namespace Comet.Core;

public sealed class DoorClosingConfig
{
    public bool Enabled {get;set;}=false;
    public double OpenGrace {get;set;}=6;
    public double OccupiedRadius {get;set;}=1.5;
    public double FailedRetry {get;set;}=10;
    public void Validate()
    {
        CometConfig.Range(OpenGrace,1,30,nameof(OpenGrace));
        CometConfig.Range(OccupiedRadius,.75,3,nameof(OccupiedRadius));
        CometConfig.Range(FailedRetry,2,60,nameof(FailedRetry));
    }
}
public static class DoorClosing
{
    // Capsule route against a hypothetical shut leaf. Do not rotate the live collider to test it.
    public static bool Blocks(DoorPoint a,DoorPoint b,DoorPoint hinge,DoorPoint tip,double radius=.3)
    {
        if(!a.Finite||!b.Finite||!hinge.Finite||!tip.Finite||!DoorPolicy.Finite(radius)||radius<0)return true;
        double Cross(DoorPoint p,DoorPoint q,DoorPoint r)=>(q.X-p.X)*(r.Z-p.Z)-(q.Z-p.Z)*(r.X-p.X);
        double c1=Cross(a,b,hinge),c2=Cross(a,b,tip),c3=Cross(hinge,tip,a),c4=Cross(hinge,tip,b);
        if(((c1>0&&c2<0)||(c1<0&&c2>0))&&((c3>0&&c4<0)||(c3<0&&c4>0)))return true;
        return Math.Min(Math.Min(DoorSpatial.SegmentDistance(a,hinge,tip),DoorSpatial.SegmentDistance(b,hinge,tip)),
            Math.Min(DoorSpatial.SegmentDistance(hinge,a,b),DoorSpatial.SegmentDistance(tip,a,b)))<=radius;
    }
}
public sealed class DoorClosingTrace
{
    public string State {get;set;}="rejected";
    public string Reason {get;set;}="closing-disabled";
    public string Door {get;set;}="";
    public string DoorState {get;set;}="";
    public double GraceRemaining {get;set;}
    public double RetryRemaining {get;set;}
    public double OccupiedRadius {get;set;}
    public double ValidFor {get;set;}
    public double X {get;set;}
    public double Y {get;set;}
    public double Z {get;set;}
}
