using Comet.Client;
using EFT.Interactive;
using SAIN.Components;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;

int checks=0;void Check(bool ok,string why){checks++;if(!ok)throw new Exception("C03 production adapter: "+why);}
var plugin=new Plugin();var bot=plugin.Add();var door=new Door();var data=new DoorDataStruct{Door=door,Link=new Link()};
Physics.World.Add(door.Collider!);
for(int degrees=0;degrees<=90;degrees+=5)
{
 double angle=degrees*Math.PI/180;door.DoorState=degrees==90?EDoorState.Open:EDoorState.Interacting;
 door.Collider!.A=new(-.5f,0,0);door.Collider.B=new((float)(-.5+Math.Cos(angle)),0,(float)Math.Sin(angle));
 Time.time+=1;
 var midpoint=new Vector3((door.Collider.A.x+door.Collider.B.x)/2,0,(door.Collider.A.z+door.Collider.B.z)/2);
 Check(plugin.Check(bot,data,midpoint)=="geometry-live-panel-clearance","actual rotated panel "+degrees);
 Check(plugin.BufferEmpty,"no retained physics references");
 Check(plugin.Geometry(data,out var frame)&&Math.Abs(frame.Center.X)<1e-9&&frame.Center.Z==0,"frame independent of moving panel");
}
door.DoorState=EDoorState.Shut;door.Collider!.A=new(-.5f,0,0);door.Collider.B=new(.5f,0,0);
Check(plugin.Check(bot,data,new(-.5f,0,.8f))=="","closed leaf does not block its theoretical open location");
Check(plugin.Check(bot,data,new(0,0,.54f))=="geometry-live-panel-clearance","inside 0.55 margin");
Check(plugin.Check(bot,data,new(0,0,.56f))=="","outside 0.55 margin");
Check(plugin.Step(bot,new(0,0,.54f))=="geometry-combat-step-panel","combat backstep rejected against live leaf");
Check(plugin.Step(bot,new(2,0,2))=="","combat step in clear space accepted");
Check(plugin.BufferEmpty,"combat step releases collider references");
plugin.Config.DoorSpatial.Clearance=.7;
Check(plugin.Check(bot,data,new(0,0,.6f))=="geometry-live-panel-clearance","server margin applied");
plugin.Config.DoorSpatial.Clearance=.55;
var second=new Door{Id="second",DoorState=EDoorState.Interacting};second.Collider!.A=new(1,0,0);second.Collider.B=new(1,0,1);Physics.World.Add(second.Collider);
Time.time+=1;Check(plugin.Check(bot,data,new(1.1f,0,.7f))=="geometry-live-panel-clearance","double/nearby door panel");
Check(plugin.Lines.Last().Contains("second"),"blocker identity recorded");
second.Collider.Low=3;second.Collider.High=5;
Check(plugin.Check(bot,data,new(1.1f,0,.7f))=="","door on other floor ignored");
Check(plugin.Check(bot,data,new(1.1f,3,.7f))=="geometry-wrong-floor","candidate on other floor rejected");
Check(plugin.Check(bot,data,new(1.1f,.4f,.7f))=="","small stair/threshold accepted");
door.Collider.enabled=false;Check(plugin.Check(bot,data,new(3,0,0))=="geometry-panel-unavailable","missing physical proof rejected");door.Collider.enabled=true;
door.Collider.isTrigger=true;Check(plugin.Check(bot,data,new(3,0,0))=="geometry-panel-unavailable","interaction trigger is not physical proof");door.Collider.isTrigger=false;
door.Collider.gameObject.activeInHierarchy=false;Check(plugin.Check(bot,data,new(3,0,0))=="geometry-panel-unavailable","inactive physical panel uncertain");door.Collider.gameObject.activeInHierarchy=true;
Physics.Throw=true;Check(plugin.Check(bot,data,new(3,0,0))=="geometry-query-failed","physics exception safe fallback");Check(plugin.BufferEmpty,"exception clears buffer");Physics.Throw=false;
for(int i=0;i<32;i++)Physics.World.Add(new Collider{A=new(3,0,0),B=new(3,0,1)});
Check(plugin.Check(bot,data,new(3,0,0))=="geometry-query-saturated","saturation cannot look safe");Check(plugin.BufferEmpty,"saturation clears references");
Physics.World.Clear();Physics.World.Add(door.Collider);
plugin.Config.DoorSpatial.Enabled=false;int before=Physics.Calls;
Check(plugin.Check(bot,data,new(-.5f,0,.8f))=="geometry-legacy-leaf-clearance","toggle uses documented legacy fallback");Check(Physics.Calls==before,"no live queries when disabled");
plugin.Config.DoorSpatial.Enabled=true;Time.time+=1;int logs=plugin.Lines.Count;
for(int i=0;i<20;i++)plugin.Check(bot,data,new(0,0,.54f));
Check(plugin.Lines.Count==logs+1,"identical log rate bound");Check(Api.Decisions.Lines.SequenceEqual(plugin.Lines),"RR bus receives exact diagnostics");
string fixture=plugin.Lines.Last();plugin.Release(bot);
// Waypoints custom links leave BottomY at zero on every floor; native links must retain their explicit floor.
data.Link.name="DoorLink_Custom_Test";data.Link.BottomY=0;
data.Link.Close1=new(-.5f,13,0);data.Link.Close2_Normal=new(-.5f,13,1);
data.Link.Open1=new(-.5f,13,0);data.Link.Open2=new(.5f,13,0);
door.Collider.Low=12;door.Collider.High=14;Time.time+=1;
Check(plugin.Geometry(data,out var upper),"custom upstairs link decodes");
door.Collider!.enabled=false;
Check(plugin.Geometry(data,out var disabledPanel)&&Math.Abs(disabledPanel.Center.Y-data.Link.Open1.y)<.01,"disabled collider falls back to world-space link height");
door.Collider.enabled=true;
Check(plugin.Check(bot,data,new(2,12,2))=="","upstairs candidate uses physical leaf floor rather than zero");
Check(plugin.Check(bot,data,new(2,0,2))=="geometry-wrong-floor","ground candidate still rejected for upstairs custom link");
data.Link.name="native";data.Link.BottomY=12;
Check(plugin.Check(bot,data,new(2,12,2))=="","native explicit floor preserved");
plugin.Release(bot);
Check(plugin.Lines.Last().Contains("\"validFor\":0"),"release clears map observation");
int ended=plugin.Lines.Count;plugin.Release(bot);Check(plugin.Lines.Count==ended,"release idempotent");
string root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));Directory.CreateDirectory(Path.Combine(root,"artifacts/c03"));
File.WriteAllLines(Path.Combine(root,"artifacts/c03/geometry.jsonl"),plugin.Lines);
File.WriteAllText(Path.Combine(root,"artifacts/c03/geometry.json"),fixture);
Console.WriteLine($"C03 production geometry adapter: {checks} checks passed");
