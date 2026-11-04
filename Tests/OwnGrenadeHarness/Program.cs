using Comet.Client;
using EFT;
using EFT.Ballistics;
using SAIN.Components;
using SAIN.Models.Enums;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
int checks=0;void C(bool v,string why){checks++;if(!v)throw new Exception("G04 adapter: "+why);}
Time.Set(0);var p=new Plugin();var owner=new BotComponent();p.Add(owner);p.states[owner].OwnGrenade.Phased=true;
var ally=new BotComponent{ProfileId="ally",Position=new(3,0,0)};var far=new BotComponent{ProfileId="far",Position=new(90,0,0)};
var scav=new BotComponent{ProfileId="scav",Pmc=false,Position=new(3,0,0)};
owner.Squad.Members.Add("ally",ally);owner.Squad.Members.Add("far",far);owner.Squad.Members.Add("scav",scav);p.Add(ally);p.states[ally].OwnGrenade.Phased=true;p.Add(far);p.Add(scav);
Grenade G(int id,float x)=>new(){Id=id,transform=new(){position=new(x,0,0)}};
var a=G(-1,0);p.Throw(a);C(p.Count==1,"own projectile captured");C(p.Trace(owner)?.State=="tracking","release recorded");C(p.Trace(ally)?.Source=="observed-squad-throw","observed ally");C(p.Trace(far)==null&&p.Trace(scav)==null,"no global awareness/non-PMC");
a.transform.position=new(2,0,0);Time.Set(.1f);p.Tick(owner);p.Tick(ally);C(owner.Grenade.GrenadeReactionClass.Reaction==EGrenadeReaction.None,"departing own grace");C(ally.Grenade.GrenadeReactionClass.Reaction==EGrenadeReaction.Scatter,"ally no grace toward itself");
a.transform.position=new(1,0,0);Time.Set(.21f);p.Tick(owner);C(owner.Grenade.GrenadeReactionClass.Reaction==EGrenadeReaction.Scatter,"rebound forces scatter even aggressive personality");C(p.Releases.Contains("own-grenade-native-escape"),"assault yielded");
var toward=p.Move(owner,new(1,0,0));var away=p.Move(owner,new(-1,0,0));C(toward.sqrMagnitude==0&&away.sqrMagnitude>0,"lateral approach blocked, escape allowed");
owner.Position=new(-14,0,0);Time.Set(.4f);p.Tick(owner);C(owner.Grenade.GrenadeReactionClass.Reaction==EGrenadeReaction.None,"out of exit radius");
var path=new BotPathDataManual{PathFinder=owner.Mover,PathPoints=new(){new(14,0,0)}};owner.Mover.ActivePath=path;C(!p.Path(path)&&path.Stopped,"safe destination but exposed route stopped immediately");
owner.Position=new(0,0,0);var b=G(-2,-2);p.Throw(b);Time.Set(.6f);p.Tick(owner);p.Explode("wrong",-1);C(p.Count==2,"wrong owner ignored");p.Explode("owner",-1);Time.Set(.8f);p.Tick(owner);C(p.Count==1&&p.Trace(owner)?.Projectile==-2&&p.Trace(owner)?.State=="fleeing","first explosion cannot release second");
p.Lose(-2);Time.Set(1);p.Tick(owner);C(p.Trace(owner)?.Outcome=="outcome-unknown","lost object not explosion");C(p.Releases.Contains("own-grenade-outcome-unknown"),"unknown cancels dependent assault");
Time.Set(11);p.Tick(owner);C(p.Count==0,"unknown expires boundedly");C(!p.diagnostics!.Lines.Any(x=>x.Contains("confirmed-explosion")&&x.Contains("\"projectile\":-2")),"no synthetic blast");
foreach(var mat in new[]{MaterialType.Glass,MaterialType.GlassShattered,MaterialType.MetalThin,MaterialType.WoodThin,MaterialType.Concrete,MaterialType.MetalThick}){
 Time.Set(12+checks);var col=new Collider();col.Parts[typeof(BallisticCollider)]=new BallisticCollider{TypeOfMaterial=mat};Physics.Hit=col;C(p.Cover()==(mat is MaterialType.Concrete or MaterialType.MetalThick),"material "+mat);
}
Physics.Hit!.Parts[typeof(EFT.Interactive.Door)]=new EFT.Interactive.Door();Time.Set(50);C(!p.Cover(),"closed door not guaranteed shield");Physics.Hit=new TerrainCollider();Time.Set(51);C(p.Cover(),"terrain cover");Physics.Hit=null;
var c=G(3,1);p.Throw(c);Time.Set(52);p.Tick(owner);p.ghosts.Add(owner.ProfileId);Time.Set(53);p.Tick(owner);C(owner.Grenade.GrenadeReactionClass.DangerGrenade==null,"ghost releases native override");p.ghosts.Clear();p.Authority=false;Time.Set(54);p.Tick(ally);C(ally.Grenade.GrenadeReactionClass.DangerGrenade==null,"remote authority releases");p.Authority=true;
p.config!.OwnGrenadeSafety.Enabled=false;Time.Set(55);p.Tick(owner);C(p.Trace(owner)?.ValidFor==0,"disabled lease closes");p.config.OwnGrenadeSafety.Enabled=true;p.End();C(p.Count==0&&owner.Grenade.GrenadeReactionClass.DangerGrenade==null,"raid teardown");
var artifacts=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../artifacts"));
Directory.CreateDirectory(artifacts);
File.WriteAllLines(Path.Combine(artifacts,"g04-adapter-events.jsonl"),p.diagnostics.Lines);
Console.WriteLine($"G04 production adapter: {checks} checks passed (controlled engine doubles, no raid simulated).");
