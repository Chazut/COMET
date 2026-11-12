using Comet.Client;
using Comet.Core;
using EFT;
using SAIN.Components;
int checks=0;void Check(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
WeaponReturnGun Gun(string id,string slot,int loaded,int reserve=0)=>new(){Id=id,Slot=slot,Loaded=loaded,Chamber=loaded>0?1:0,Magazine=Math.Max(0,loaded-1),Capacity=31,Reserve=reserve,Resource=reserve>0?"magazine":"none",Usable=true};
var p=new Plugin();var b=new BotComponent();SAIN.SAINEnableClass.Bot=b;
var selector=new BotWeaponSelector{_owner=b.BotOwner};b.BotOwner!.WeaponManager!.Selector=selector;
// Raid regression: loaded Tavor in hand, preferred vz.58 empty with no reserve.
var frame=new WeaponReturnFrame{Now=327.65,Current=Gun("tavor","SecondPrimaryWeapon",17),Main=Gun("vz58","FirstPrimaryWeapon",0),Ready=true,ContactAge=999};
frame.Other=frame.Current;p.Frame=frame;
for(int i=0;i<1178;i++)
{
 frame.Now=327.65+i*.66;
 Check(!p.Request(selector,out var result)&&!result,"empty-main return stays rejected before slot request");
}
Check(selector.Requests==0&&p.Errors==0,"no native requests or exceptions during repeated empty-main return");
Check(p.states[b].WeaponReturn.Policy.Trace!.Reason=="weapon-target-unusable","reason remains visible");
frame.Main=Gun("vz58","FirstPrimaryWeapon",10);frame.Now+=1;
Check(p.Request(selector,out _)&&selector.Requests==0,"usable main passes through to native selector");
foreach(var mode in new[]{"disabled","non-pmc","replica","ghost","inactive","ending","no-sain-layer","medicine","grenade","melee","empty-hands"})
{
 var current=new Plugin{Frame=frame};SAIN.SAINEnableClass.Bot=b;b.Player.HandsController=new Player.FirearmController();
 b.Pmc=b.BotActive=b.SAINLayersActive=true;b.GameEnding=false;
 if(mode=="disabled")current.config!.WeaponReturn.Enabled=false;
 if(mode=="non-pmc")b.Pmc=false;
 if(mode=="replica")current.Authority=false;
 if(mode=="ghost")current.IsGhost=true;
 if(mode=="inactive")b.BotActive=false;
 if(mode=="ending")b.GameEnding=true;
 if(mode=="no-sain-layer")b.SAINLayersActive=false;
 if(mode is "medicine" or "grenade" or "melee")b.Player.HandsController=new object();
 if(mode=="empty-hands")b.Player.HandsController=null;
 int requests=selector.Requests;
 Check(current.Request(selector,out _)&&selector.Requests==requests,"native behavior retained: "+mode);
}
var ready=new Plugin();b.GameEnding=b.IsDead=false;var hands=new Player.FirearmController();
Check(ready.Ready(b,hands),"healthy hands ready");
hands.Reload=true;Check(ready.Ready(b,hands),"native selector readiness authoritative");hands.Reload=false;
b.GameEnding=true;Check(!ready.Ready(b,hands),"raid teardown not ready");b.GameEnding=false;
selector.IsChanging=true;Check(ready.Ready(b,hands),"no duplicate changing veto");selector.IsChanging=false;
b.BotOwner!.WeaponManager!.Selector=null;Check(!ready.Ready(b,hands),"disposed selector not ready");
b.BotOwner.WeaponManager=null;Check(!ready.Ready(b,hands),"disposed manager not ready");
b.BotOwner=null;Check(!ready.Ready(b,hands),"disposed owner not ready");
Console.WriteLine($"W03 production request/readiness methods: {checks} checks. Native calls doubled; no Unity raid.");
