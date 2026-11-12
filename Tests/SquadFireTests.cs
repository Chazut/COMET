using Comet.Core;
internal static class SquadFireTests { public static int Run(){int n=0;void C(bool v,string m){n++;if(!v)throw new Exception("SquadFire: "+m);}
var c=new SquadFireConfig();var p=new SquadFirePolicy();
C(p.Tick(new(){Now=1,Fresh=true,Risk=true,Ally="a",Kind="prediction"},c),"predicted squadmate blocks");
C(!p.Tick(new(){Now=1.01,Fresh=true,Risk=false},c),"clear resumes immediately");
p.Tick(new(){Now=2,Fresh=true,Risk=true},c);C(!p.Tick(new(){Now=2.01,Fresh=false},c),"no stale veto");
var start=new DoorPoint(0,1,0);var end=new DoorPoint(10,1,0);
C(SquadFireGeometry.Crosses(start,end,new(5,1,2),new(5,1,.54),.55),"predicted endpoint inside radius");
C(!SquadFireGeometry.Crosses(start,end,new(5,1,2),new(5,1,-2),.55),"no COMET swept prediction");
C(!SquadFireGeometry.Crosses(start,end,new(0,1,0),new(.29,1,0),.55),"source near muzzle cutoff");
C(SquadFireGeometry.Crosses(start,end,new(0,1,0),new(.3,1,0),.55),"source muzzle boundary");
C(!SquadFireGeometry.Crosses(start,end,new(0,1,0),new(10.01,1,0),.55),"behind target");
return n;}}
