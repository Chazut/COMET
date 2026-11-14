using Comet.Client;
using EFT;
using SAIN.Components;
using SAIN.Models.Enums;
using UnityEngine;
int checks=0;void C(bool v,string why){checks++;if(!v)throw new Exception("G04 adapter: "+why);}
Time.Set(0);var p=new Plugin();var owner=new BotComponent();p.Add(owner);p.states[owner].OwnGrenade.Phased=true;
var ally=new BotComponent{ProfileId="ally",Position=new(3,0,0)};p.Add(ally);owner.Squad.Members.Add("ally",ally);
Grenade G(int id,float x){var g=new Grenade{Id=id,ProfileId=owner.ProfileId,WeaponSource=new EFT.InventoryLogic.ThrowWeap()};g.transform.position=new(x,0,0);return g;}
var a=G(-1,2);p.Throw(a);C(p.Count==1,"own tracked");
Time.Set(.1f);p.Tick(owner);p.Tick(ally);C(owner.Grenade.GrenadeReactionClass.Reaction==EGrenadeReaction.None,"source departure grace");
C(p.Trace(ally)==null,"squad uses native grenade awareness");
C(p.Threat(owner,new(1,0,0)),"explicit destination safety has no reaction grace");
C(!p.Threat(ally,new(1,0,0)),"own check does not create shared awareness");
Time.Set(.8f);p.Tick(owner);C(owner.Grenade.GrenadeReactionClass.Reaction==EGrenadeReaction.Scatter,"source own danger");
owner.Position=new(-9,0,0);Time.Set(1);p.Tick(owner);C(owner.Grenade.GrenadeReactionClass.Reaction==EGrenadeReaction.None,"outside keep radius");
owner.Position=new(0,0,0);var b=G(-2,-2);p.Throw(b);Time.Set(1.8f);p.Tick(owner);p.Explode("wrong",-1);C(p.Count==2,"wrong owner ignored");p.Explode(owner.ProfileId,-1);Time.Set(2);p.Tick(owner);C(p.Count==1,"one explosion does not retire another");
p.Lose(-2);Time.Set(2.2f);p.Tick(owner);Time.Set(2.4f);p.Tick(owner);C(p.Count==0&&owner.Grenade.GrenadeReactionClass.DangerGrenade==null,"lost object retires source danger");
C(!p.diagnostics!.Lines.Any(x=>x.Contains("confirmed-explosion")&&x.Contains("\"projectile\":-2")),"no synthetic blast");
Physics.Hit=new Collider();C(p.Cover(),"source accepts any solid highpoly ray hit");Physics.Hit=null;C(!p.Cover(),"open ray");
Time.Set(3);p.Throw(G(3,1));Time.Set(3.8f);p.Tick(owner);p.ghosts.Add(owner.ProfileId);Time.Set(4);p.Tick(owner);C(owner.Grenade.GrenadeReactionClass.DangerGrenade==null,"ghost releases override");p.ghosts.Clear();
Time.Set(12);p.Tick(owner);C(p.Count==0,"live object expires after eight seconds");
p.End();C(p.Count==0,"raid cleanup");
var nativeSmoke=new SmokeGrenade{Id=-4,ProfileId=owner.ProfileId,WeaponSource=new EFT.InventoryLogic.ThrowWeap()};
p.Throw(nativeSmoke);C(p.Count==0,"runtime smoke is not tracked as own explosive even with frag throw type");
var collisionSmoke=G(-5,2);collisionSmoke.GrenadeSettings.CollisionSound=GrenadeSettings.CollisionSounds.smoke;
p.Throw(collisionSmoke);C(p.Count==0,"collision smoke is not tracked as own explosive even with frag throw type");
Console.WriteLine($"G04 production adapter: {checks} source checks passed; engine doubles, no Unity raid.");
