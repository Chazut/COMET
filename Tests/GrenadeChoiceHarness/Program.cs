using Comet.Client;
using Comet.Core;
using EFT;
using EFT.InventoryLogic;
using SAIN.Components;
using UnityEngine;

string wire=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../artifacts/g03-adapter-events.jsonl"));File.WriteAllText(wire,"");
int checks=0;void Check(bool ok,string why){checks++;if(!ok)throw new Exception("G03 adapter: "+why);}
(Plugin,BotComponent) Setup(bool g01=true,bool g02=true)
{
 var p=new Plugin();p.Settings.GrenadeJudgment.Enabled=g01;p.Settings.GrenadeTrajectory.Enabled=g02;
 var b=new BotComponent();b.Player.Inventory.Items.Clear();
 b.BotOwner.WeaponManager.Grenades.grenade=new ThrowWeap{Id="long",GetExplDelay=5};
 b.Player.Inventory.Items.Add(b.BotOwner.WeaponManager.Grenades.grenade);
 b.Player.Inventory.Items.Add(new ThrowWeap{Id="short",GetExplDelay=3});p.Tick(b,10);return(p,b);
}
bool Ready(Plugin p,BotComponent b)
{
 for(int i=0;i<180;i++)
 {if(p.Find(b))return true;if(p.State(b).GrenadeChoice.Trace?.State=="blocked"||p.State(b).GrenadeTrajectory.Trace?.State=="blocked")return false;p.Tick(b,Time.timeAsDouble+.01);}
 return false;
}
foreach(bool g01 in new[]{false,true})foreach(bool g02 in new[]{false,true})
{
 var(p,b)=Setup(g01,g02);b.Player.Environment=EnvironmentType.Indoor;
 bool ready=Ready(p,b);if(!ready)Console.WriteLine(string.Join("\n",p.Events));
 Check(ready,$"short indoor selection with independent G01={g01}/G02={g02} toggles");var r=p.State(b).GrenadeChoice;
 Check(r.Item=="short"&&r.Trace!.Margin>=.75,"real shorter carried item selected");
 Check(b.BotOwner.WeaponManager.Grenades.AIGreanageThrowData!.GrenadeType==null,"native type replacement disabled for selected arc");
 int draws=p.Draws,selections=b.BotOwner.WeaponManager.Grenades.Selections;
 Check(p.Find(b)&&p.Draws==draws&&b.BotOwner.WeaponManager.Grenades.Selections==selections,"selection and probability stay stable");
 p.Tick(b,Time.timeAsDouble+.01);Check(p.Throw(b)&&p.Requests==1,"native throw is authorized after validation");
 Check(r.Trace!.State=="validated"&&r.Trace.Margin>=.75,"validation trace does not claim projectile release");
 if(g01){p.Projectile(b,"short");Check(p.State(b).GrenadeJudgment.Policy.Trace!.Released,"selected item matches projectile callback");}
 p.End(b);Check(r.Item==""&&r.Trace.State=="ended"&&r.Trace.ValidFor==0,"raid cleanup clears binding");
 File.AppendAllLines(wire,p.Events);
}
foreach(var changed in new Action<Plugin,BotComponent>[] {
 (p,b)=>b.Player.Inventory.Items.Remove(b.BotOwner.WeaponManager.Grenades.grenade!),
 (p,b)=>b.BotOwner.WeaponManager.Grenades.grenade!.GetExplDelay=1,
 (p,b)=>b.BotOwner.WeaponManager.Grenades.grenade!.MinTimeToContactExplode=0,
 (p,b)=>b.BotOwner.WeaponManager.Grenades.grenade!.IsDummy=true,
 (p,b)=>b.BotOwner.WeaponManager.Grenades.grenade=new ThrowWeap{Id="imposter"},
 (p,b)=>p.Players.Add(new Player{Position=b.BotOwner.WeaponManager.Grenades.AIGreanageThrowData!.Target}),
 (p,b)=>NativeAdapter.FriendsClear=false,(p,b)=>p.Settings.Revision++,
 (p,b)=>b.GoalEnemy=new(),(p,b)=>b.GoalEnemy!.KnownPlaces.LastKnownPlace=new(),
 (p,b)=>b.BotOwner.Medecine.Using=true,(p,b)=>b.BotOwner.WeaponManager.Grenades.ThrowindNow=true,
 (p,b)=>b.BotOwner.WeaponManager.Grenades.AIGreanageThrowData!.Force=.1f,
 (p,b)=>b.BotOwner.WeaponManager.Grenades.AIGreanageThrowData!.ThrowComplete=true,
 (p,b)=>b.BotOwner.WeaponManager.Grenades.AIGreanageThrowData!.Fresh=false,
 (p,b)=>b.BotOwner.WeaponManager.Grenades.AIGreanageThrowData!.Ang=float.NaN,
 (p,b)=>b.BotOwner.WeaponManager.Grenades.MaxPower=2,
 (p,b)=>Physics.BlockArc=true})
{
 var(p,b)=Setup(false,false);Check(Ready(p,b),"ready before changed facts");p.Tick(b,Time.timeAsDouble+.01);changed(p,b);
 Check(!p.Throw(b)&&p.Requests==0,"revalidation blocks stale, changed, unsafe item/arc/facts with G01/G02 OFF");
}
foreach(var exclude in new Action<Plugin,BotComponent>[] {(p,b)=>p.Settings.Enabled=false,(p,b)=>p.Authority=false,(p,b)=>p.IsGhost=true,(p,b)=>b.Pmc=false,(p,b)=>b.IsDead=true,(p,b)=>b.GameEnding=true,(p,b)=>b.BotActive=false})
{var p=new Plugin();var b=new BotComponent();exclude(p,b);p.Tick(b,10);Check(p.Count==0&&p.Find(b)&&p.Throw(b)&&b.BotOwner.WeaponManager.Grenades.Selections==0,"excluded bots/clients retain native pipeline");}
{
 var(p,b)=Setup(false,false);p.Settings.GrenadeChoice.Enabled=false;Check(p.Find(b)&&p.Count==1&&b.BotOwner.WeaponManager.Grenades.Selections==0,"G03 disabled leaves item unchanged");
}
{
 var(p,b)=Setup(false,false);b.Player.Environment=EnvironmentType.Indoor;
 b.BotOwner.WeaponManager.Grenades.AIGreanageThrowData!.Force=9;
 Check(Ready(p,b)&&p.State(b).GrenadeChoice.Item=="long","unsafe short fuse rejected despite preference");
 Check(p.State(b).GrenadeChoice.Trace!.Alternatives.Any(a=>a.Item=="short"&&a.Reason=="insufficient-arrival-margin"),"rejected alternative reason recorded");
}
{
 var(p,b)=Setup(false,false);b.GoalEnemy!.IsVisible=false;b.GoalEnemy.EnemyPlayer.Environment=EnvironmentType.Indoor;
 Check(Ready(p,b)&&p.State(b).GrenadeChoice.Item=="long","unseen target room cannot influence selection");
}
{
 var(p,b)=Setup(false,false);b.GoalEnemy!.EnemyPlayer.Environment=EnvironmentType.Indoor;p.Tick(b,10.3);
 Check(Ready(p,b)&&p.State(b).GrenadeChoice.Item=="short","personally observed room works with G01 off");
}
{
 var(p,b)=Setup();b.Player.Environment=EnvironmentType.Indoor;Check(Ready(p,b),"choice bound to prepared G02 arc");
 Check(p.State(b).GrenadeTrajectory.Item=="short"&&p.State(b).GrenadeJudgment.ArcItem=="short","intentional selection updates arc item fingerprints");
 int draws=p.Draws;double? roll=p.State(b).GrenadeJudgment.Policy.Trace!.Roll;
 p.Tick(b,Time.timeAsDouble+.1);Check(p.Find(b)&&p.Draws==draws&&p.State(b).GrenadeJudgment.Policy.Trace!.Roll==roll,"item choice creates no extra G01 roll");
 b.BotOwner.WeaponManager.Grenades.ThrowindNow=true;int selections=b.BotOwner.WeaponManager.Grenades.Selections;
 Check(!p.Find(b)&&b.BotOwner.WeaponManager.Grenades.Selections==selections,"no swap during animation");
}
foreach(var changed in new Action<Plugin,BotComponent>[] {
 (p,b)=>{for(int i=0;i<33;i++)b.Player.Inventory.Items.Add(new ThrowWeap{Id="extra"+i});},
 (p,b)=>{for(int i=0;i<256;i++)b.Player.Inventory.Items.Add(new Item());},
 (p,b)=>b.Player.Inventory.Items.Add(new ThrowWeap{Id="short"}),
 (p,b)=>{foreach(var item in b.Player.Inventory.Items.OfType<ThrowWeap>())item.GetExplDelay=1;}})
{var(p,b)=Setup(false,false);changed(p,b);Check(!Ready(p,b)&&p.State(b).GrenadeChoice.Trace!.State=="blocked"&&b.BotOwner.WeaponManager.Grenades.Selections==0,"bounded inventory rejects invalid/ambiguous/no-safe choices");}
Console.WriteLine($"G03 production adapter harness: {checks} checks passed (native contracts, not Unity/Fika raid).");
