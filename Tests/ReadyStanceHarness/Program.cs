using Comet.Client;using EFT;using SAIN.Components;using SAIN.Models.Enums;using SAIN.Preset.Shared.Enums;using UnityEngine;
int n=0;void C(bool v,string m){n++;if(!v)throw new Exception(m);}
foreach(var layer in new[]{ESAINLayer.Combat,ESAINLayer.AvoidThreat,ESAINLayer.Flashed}){
 Time.time=10;var p=new Plugin();var b=new BotComponent{ActiveLayer=layer};p.Fight(b);b.ActiveLayer=ESAINLayer.None;
 C(!p.Wish(b,true),"recent combat retains raised stance");C(p.Wish(b,false)==false,"native false unchanged");
 Time.time=35;C(!p.Wish(b,true),"inclusive 25 second boundary");Time.time=35.01f;C(p.Wish(b,true),"native patrol restored");
 p.End(b);C(!p.states[b].ReadyStance.Policy.Pending(Time.time),"cleanup");
}
{Time.time=100;var p=new Plugin();var b=new BotComponent();p.Fight(b);C(p.Wish(b,true),"no combat means no veto");}
{Time.time=10;var p=new Plugin();var b=new BotComponent{ActiveLayer=ESAINLayer.Combat};p.IsGhost=true;p.Fight(b);C(p.Wish(b,true)&&p.states.Count==0,"ghost stays out of scope");}
Console.WriteLine($"H05 source adapter: {n} checks passed");
