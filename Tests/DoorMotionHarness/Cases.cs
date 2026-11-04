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
  Plugin.RecoveryHooks();Plugin.Hooks();Plugin.MotionHooks();
  (Plugin p,BotComponent b,BotState s,Door d) Setup(float angle=0)
  {
   Physics.Occupied=null;Physics.Queries=0;DoorMotionNetwork.Ready=null;
   Singleton<GameWorld>.Instance=new();var p=new Plugin();Plugin.Instance=p;p.At(0);
   var b=new BotComponent();b.Player.IsAI=true;b.Position=b.Player.Position=new(0,0,-.8f);
   b.Mover.ActivePath=new BotPathDataManual(b);var s=new BotState();p.states[b]=s;
   var link=new Link();b.BotOwner.VoxelesPersonalData.CurVoxel!.DoorLinks.Add(link);
   var d=link.Door;d.CurrentAngle=angle;d.DoorState=angle==0?EDoorState.Shut:EDoorState.Open;
   Singleton<GameWorld>.Instance.Doors[d.Id]=d;Singleton<GameWorld>.Instance.AllAlivePlayersList.Add(b.Player);
   return(p,b,s,d);
  }
  var x=Setup();Check(x.p.Push(x.d,x.b)==-90,"open pushed away from PMC");Check(x.p.Swings==1,"pending push watched");
  x.d.DoorState=EDoorState.Open;x.d.CurrentAngle=-90;x.p.MotionTick(.5f);Check(x.p.Swings==0,"push confirmation releases references");
  var fixture=x.p.diagnostics!.Lines.ToList();
  x=Setup();x.p.FillSwings();Check(x.p.Push(x.d,x.b)==90,"full watch capacity cannot author untracked angle");
  x=Setup();x.p.Push(x.d,x.b);Check(x.p.Push(x.d,x.b)==90&&x.p.Swings==1,"duplicate interaction cannot escape its watcher");
  x=Setup();x.b.Player.Position=new(0,0,.8f);Check(x.p.Push(x.d,x.b)==90,"already opening away unchanged");
  x=Setup();Physics.Occupied=v=>v.z>.2;Check(x.p.Push(x.d,x.b)==90,"reverse sweep wall prevents flip");
  x=Setup();Check(x.p.Push(x.d,x.b,true)==-90&&x.p.Swings==0,"failed interaction clears scoped context");
  x=Setup();x.b.Pmc=false;Check(x.p.Push(x.d,x.b)==90,"non-PMC untouched");
  x=Setup();x.p.Authority=false;Check(x.p.Push(x.d,x.b)==90,"remote bot cannot author motion");
  x=Setup();x.p.IsGhost=true;Check(x.p.Push(x.d,x.b)==90,"ghost excluded");
  x=Setup();x.p.config!.DoorMotion.PushAway=false;Check(x.p.Push(x.d,x.b)==90,"push toggle");
  x=Setup();x.p.fikaPresent=true;Check(x.p.Push(x.d,x.b)==90,"missing Fika transport prevents local-only angle changes");
  DoorMotionNetwork.Ready=()=>true;Check(x.p.Push(x.d,x.b)==-90,"ready custom transport permits reverse opening");
  x=Setup(-90);x.b.Position=x.b.Player.Position=new(-.55f,0,.9f);x.p.MotionTick(0);x.p.MotionTick(1);Check(x.p.Swings==0,"unjam waits for actual lack of movement");
  x.p.MotionTick(1.5f);Check(x.p.Swings==1,"reversed nearby leaf unjam scheduled");
  x.p.MotionTick(1.75f);Check(Math.Abs(x.d.CurrentAngle)<.01,"physical halfway correction");
  x.p.MotionTick(2);Check(x.d.CurrentAngle==90&&x.p.Swings==0,"smooth correction reaches normal angle");fixture.AddRange(x.p.diagnostics!.Lines);
  Check(EFT.GlobalEvents.InteractiveObjectInteractionResultEvent.Calls>0,"completed forced swing notifies game listeners");
  x=Setup(-90);x.b.Position=x.b.Player.Position=new(-.55f,0,.9f);x.p.MotionTick(0);x.b.Position=new(1,0,.9f);x.p.MotionTick(1.5f);Check(x.p.Swings==0,"progress resets stalled interval");
  x=Setup(-90);x.b.Position=x.b.Player.Position=new(-.55f,0,.9f);Singleton<GameWorld>.Instance.AllAlivePlayersList.Add(new(){IsAI=false,Position=new(1,0,0)});x.p.MotionTick(0);x.p.MotionTick(1.5f);Check(x.p.Swings==0,"nearby human protects swing");
  x=Setup(-90);x.b.Position=x.b.Player.Position=new(-.55f,0,.9f);x.p.MotionTick(0);x.p.MotionTick(1.5f);Physics.Occupied=_=>true;x.p.MotionTick(1.75f);Check(x.p.Swings==0&&x.d.CurrentAngle==-90,"new obstruction cancels moving leaf");
  x=Setup(-90);x.b.Position=x.b.Player.Position=new(-.55f,0,.9f);x.p.MotionTick(0);x.p.MotionTick(1.5f);x.p.MotionEnd();Check(x.p.Swings==0,"raid cleanup drops motion references");
  x=Setup();Check(DoorMotionNetwork.Apply("door",(int)EDoorState.Open,-90)&&x.d.CurrentAngle==-90,"remote exact reverse angle applied");
  Check(x.d.Native.ResultAngle==-90&&x.d.Native.Break,"remote interpolation stopped and record aligned");
  Check(!DoorMotionNetwork.Apply("door",(int)EDoorState.Open,float.NaN),"nonfinite packet rejected");
  x.d.DoorState=EDoorState.Locked;Check(!DoorMotionNetwork.Apply("door",(int)EDoorState.Open,90),"remote packet cannot unlock");
  x=Setup();x.b.Player.IsYourPlayer=true;x.b.Player.CurrentStateName=EPlayerState.DoorInteraction;x.b.Player.MovementContext.InteractionInfo.WorldInteractiveObject=x.d;
  Check(!DoorMotionNetwork.Apply("door",(int)EDoorState.Open,90),"local human interaction wins over remote correction");
  var output=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../artifacts/c04"));Directory.CreateDirectory(output);
  File.WriteAllLines(Path.Combine(output,"motion.jsonl"),fixture);File.WriteAllText(Path.Combine(output,"motion.json"),"["+string.Join(",",fixture)+"]");
  Console.WriteLine($"C04 actual motion/replica adapter: {checks} checks passed, {fixture.Count} production events.");
 }
}
