using Comet.Client;
using Comet.Core;
using Comet.Api;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using SAIN.Components;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;

static class Cases
{
 internal static void Run()
 {
  int checks=0;void Check(bool x,string why){checks++;if(!x)throw new Exception(why);}
  Plugin.RecoveryHooks();Plugin.Hooks();Plugin.MotionHooks();Plugin.ClosingHooks();
  (Plugin p,BotComponent b,BotState s,Door d,DoorDataStruct data) Setup(bool enabled=true)
  {
   Physics.Occupied=null;Physics.Queries=0;DoorMotionNetwork.Ready=null;Singleton<GameWorld>.Instance=new();
   var p=new Plugin();Plugin.Instance=p;p.At(20);p.config!.DoorClosing.Enabled=enabled;
   var b=new BotComponent();b.Player.IsAI=true;b.Position=b.Player.Position=new(-1,0,-.8f);
   b.Mover.ActivePath=new BotPathDataManual(b){PathCorners=new(){new(){Position=new(2,0,-.8f)}}};
   var s=new BotState();p.states[b]=s;var link=new Link();var d=link.Door;d.CurrentAngle=90;d.DoorState=EDoorState.Open;
   Physics.Blocker=d.Collider;Singleton<GameWorld>.Instance.AllAlivePlayersList.Add(b.Player);
   return(p,b,s,d,new(link));
  }
  var x=Setup(false);Check(!x.p.SelectClose(x.b,x.data)&&x.p.LastReason=="closing-disabled","default OFF blocks movement closing");
  x.p.config!.DoorTransit.Enabled=false;Check(!x.p.SelectClose(x.b,x.data),"OFF independent of C01");
  var n=x.p.diagnostics!.Lines.Count;for(int i=0;i<50;i++)x.p.SelectClose(x.b,x.data);Check(x.p.diagnostics.Lines.Count==n,"disabled attempts do not spam each frame");
  Check(x.p.TryClose(x.b,x.data)&&x.p.ClosingCount==0,"explicit tactical close has no movement token and is untouched");
  x=Setup();Check(x.p.SelectClose(x.b,x.data)&&x.p.LastState=="considered","blocking leaf can be considered when shut leaf clears route");
  Check(x.p.TryClose(x.b,x.data)&&x.p.ClosingCount==1&&x.p.LastState=="requested","accepted interaction only requested");
  var fixture=x.p.diagnostics!.Lines.ToList();x.p.ClosingTick(20.1f);Check(x.p.LastState=="requested","animation not mistaken for completed close");
  x.d.DoorState=EDoorState.Shut;x.d.CurrentAngle=10;x.p.ClosingTick(20.3f);Check(x.p.ClosingCount==1,"state without angle does not confirm");
  x.d.CurrentAngle=0;x.p.ClosingTick(20.5f);Check(x.p.LastState=="confirmed"&&x.p.ClosingCount==0,"observed shut state and angle confirm");fixture.Add(x.p.diagnostics.Lines.Last());
  x=Setup();x.p.Opened(x.b,x.d);Check(!x.p.SelectClose(x.b,x.data)&&x.p.LastReason=="closing-squad-open-grace","self-opening protected");
  x.p.At(25.9f);Check(!x.p.SelectClose(x.b,new(x.data.Link)),"rebuilt door data retains opening stamp");
  x.p.At(26);Check(x.p.SelectClose(x.b,x.data),"six seconds grace expires");
  x=Setup();var mate=new BotComponent();var ms=new BotState{Id="mate"};x.p.states[mate]=ms;x.p.Opened(mate,x.d);
  Check(!x.p.SelectClose(x.b,x.data),"squad opening protected");
  mate.Group="enemy";x=Setup();x.p.states[mate]=ms;x.p.Opened(mate,x.d);Check(x.p.SelectClose(x.b,x.data),"unrelated opening does not impersonate squad");
  x=Setup();x.p.Opened(x.b,x.d,true);Check(x.p.SelectClose(x.b,x.data),"exceptional open does not claim it opened");
  x=Setup();x.p.Busy=true;Check(!x.p.SelectClose(x.b,x.data)&&x.p.LastReason=="closing-tactical-priority","reservation priority");
  x=Setup();x.d.CurrentAngle=-90;Check(!x.p.SelectClose(x.b,x.data)&&x.p.LastReason=="closing-motion-priority","reversed leaf left to C04");
  x=Setup();Singleton<GameWorld>.Instance.AllAlivePlayersList.Add(new(){Position=new(1.4f,0,0)});
  Check(!x.p.CanClose(x.b,x.data)&&x.p.LastReason=="closing-doorway-occupied","human in doorway");
  Singleton<GameWorld>.Instance.AllAlivePlayersList[1].IsAI=true;x.p.At(20.1f);Check(!x.p.CanClose(x.b,x.data),"bot in doorway equally protected");
  Singleton<GameWorld>.Instance.AllAlivePlayersList[1].Position=new(0,3,0);x.p.At(20.2f);Check(x.p.CanClose(x.b,x.data),"other floor excluded");
  x=Setup();Physics.Blocker=null;Check(!x.p.CanClose(x.b,x.data)&&x.p.LastReason=="closing-leaf-not-blocking","no unnecessary close");
  x=Setup();x.b.Position=x.b.Player.Position=new(.5f,0,-.8f);((BotPathDataManual)x.b.Mover.ActivePath!).PathCorners=new(){new(){Position=new(.5f,0,2)}};
  Check(!x.p.CanClose(x.b,x.data)&&x.p.LastReason=="closing-shut-leaf-blocks-route","close-reopen traversal loop refused");
  x=Setup();Physics.Occupied=v=>v.x>.2;Check(!x.p.CanClose(x.b,x.data)&&x.p.LastReason=="closing-sweep-occupied","mid-swing obstacle");
  x=Setup();x.p.SelectClose(x.b,x.data);Physics.Occupied=_=>true;Check(!x.p.TryClose(x.b,x.data)&&x.p.ClosingCount==0,"recheck immediately before interaction");
  x=Setup();x.p.SelectClose(x.b,x.data);x.p.TryClose(x.b,x.data,false);Check(x.p.LastState=="failed","SAIN request failure recorded");
  x.p.At(29.9f);Check(!x.p.SelectClose(x.b,x.data)&&x.p.LastReason=="closing-failed-retry","failed request cooldown");
  x.p.At(30);Check(x.p.SelectClose(x.b,x.data),"retry after ten seconds");
  x=Setup();x.p.SelectClose(x.b,x.data);x.p.TryClose(x.b,x.data,true,true);Check(x.p.LastState=="failed","thrown request failure tracked");
  x=Setup();x.p.SelectClose(x.b,x.data);x.p.TryClose(x.b,x.data);x.p.ClosingTick(34);Check(x.p.ClosingCount==0&&x.p.LastReason=="closing-confirmation-timeout","unconfirmed close fails without claiming success");
  x=Setup();x.p.SelectClose(x.b,x.data);x.p.TryClose(x.b,x.data);x.d.InitializeSmoothOpenInteraction(EDoorState.Open);x.p.ClosingTick(21);
  Check(x.p.ClosingCount==0&&x.p.LastState=="ended","new interaction supersedes watcher");
  x=Setup();x.p.SelectClose(x.b,x.data);x.b.CurrentAction=new object();Check(!x.p.TryClose(x.b,x.data),"changed action invalidates queued request");
  x=Setup();x.p.SelectClose(x.b,x.data);x.p.config!.DoorClosing.Enabled=false;Check(!x.p.TryClose(x.b,x.data),"toggle rechecked before request");
  x=Setup();x.p.config!.DoorTransit.Enabled=false;Check(x.p.CanClose(x.b,x.data),"safe ON closing independent of C01");
  x=Setup();x.b.DoorOpener.Interacting=true;Check(x.p.SelectClose(x.b,x.data)&&x.p.diagnostics!.Lines.Count==0,"ongoing interaction not cancelled by selection gate");
  x=Setup();Check(x.p.SelectClose(x.b,x.data,true,EInteractionType.Open),"opening unaffected");
  x=Setup();x.p.SelectClose(x.b,x.data);x.p.ClosingTick(21);Check(x.p.TryClose(x.b,x.data)&&x.p.ClosingCount==0,"unused selection expires without capturing unrelated later interaction");
  foreach(var scope in new Action<Plugin,BotComponent>[] {(p,b)=>p.Authority=false,(p,b)=>p.IsGhost=true,(p,b)=>b.Pmc=false,(p,b)=>p.config!.Enabled=false})
  {x=Setup();scope(x.p,x.b);Check(x.p.SelectClose(x.b,x.data)&&x.p.diagnostics!.Lines.Count==0,"out of scope untouched");}
  x=Setup();x.p.fikaPresent=true;Check(!x.p.SelectClose(x.b,x.data)&&x.p.LastReason=="closing-fika-not-ready","Fika missing peer guard");
  DoorMotionNetwork.Ready=()=>true;Check(x.p.SelectClose(x.b,x.data),"ready authoritative Fika permits normal synchronized close");
  x.d.ForceLocalInteraction=true;Check(!x.p.SelectClose(x.b,x.data),"never close only locally under Fika");
  x=Setup();Singleton<GameWorld>.Instance.Doors[x.d.Id]=x.d;
  Check(DoorMotionNetwork.Apply(x.d.Id,(int)EDoorState.Shut,0)&&x.d.DoorState==EDoorState.Shut&&x.d.CurrentAngle==0,"Fika shared correction supports a completed C02-assisted C05 close");
  x=Setup();x.p.Opened(x.b,x.d);x.p.ClosingEnd();Check(x.p.SelectClose(x.b,x.data),"raid end drops opening history");
  x.p.TryClose(x.b,x.data);x.p.ClosingEnd();Check(x.p.ClosingCount==0,"raid end releases pending scene objects");
  var output=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../artifacts/c05"));Directory.CreateDirectory(output);
  File.WriteAllLines(Path.Combine(output,"closing.jsonl"),fixture);File.WriteAllText(Path.Combine(output,"closing.json"),"["+string.Join(",",fixture)+"]");
  Console.WriteLine($"C05 actual closing adapter: {checks} checks passed, {fixture.Count} production events.");
 }
}
