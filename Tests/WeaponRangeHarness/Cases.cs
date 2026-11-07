using System;
using System.Collections.Generic;
using Comet.Core;
using EFT.InventoryLogic;
public partial class Probe
{
 public static void Run()
 {
  int checks=0;void C(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
  JsonTypes.TypeTable["55818ac54bdc2d5b648b456e"]=typeof(Iron);
  JsonTypes.TypeTable["55818ae44bdc2dde698b456c"]=typeof(Scope);
  JsonTypes.TypeTable["55818ad54bdc2ddc698b4569"]=typeof(Dot);
  JsonTypes.TemplateTypeTable["55818ae44bdc2dde698b456c"]=typeof(ScopeTemplate);
  WeaponRangeGun Read(params Mod[] mods)=>ReadRangeGun(new Weapon{Mods=mods},new WeaponReturnGun{Id="gun",Loaded=30,Usable=true});
  C(Read().Known&&Read().Optic=="iron","unscoped standard rifle stays known");
  C(Read(new Iron{Sight=new()}).Known&&Read(new Iron{Sight=new()}).Optic=="iron","iron sight component is not an unknown optic");
  var scoped=Read(new Scope{Sight=new()},new Iron{Sight=new()});
  C(scoped.Known&&scoped.Optic=="scope","backup iron sight does not invalidate mounted scope");
  C(Read(new Scope(),new Dot(),new Iron{Sight=new()}).Optic=="scope-and-dot","all mounted sight families combined");
  var template=Read(new Mod{Template=new ScopeTemplate(),Sight=new()});
  C(template.Known&&template.Optic=="scope","template lookup tests the template, not the item instance");
  C(!Read(new Mod{Sight=new()}).Known,"unidentified functional sight remains conservative");
  var w=new Weapon{Mods=new Mod[]{new Iron{Sight=new()}}};var ammo=new WeaponReturnGun{Id="gun",Loaded=30,Usable=true};
  var before=ReadRangeGun(w,ammo);w.Mods=new Mod[]{new Scope{Sight=new()}};
  var after=ReadRangeGun(w,ammo);C(before.Fingerprint!=after.Fingerprint&&after.Optic=="scope","attachment changes on undrawn weapon are read immediately");
  C(Read(System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(System.Linq.Enumerable.Range(0,65),_=>new Mod()))).Optic=="unknown","oversized attachment tree is bounded");
  C(Read(new Mod{Sight=new(),TemplateId="custom-sight"}).OpticEvidence.Contains("custom-sight"),"unknown attachment trace includes its template for diagnosis");
  Console.WriteLine($"W05 equipment production methods: {checks} checks passed (native types doubled).");
 }
}
namespace EFT.InventoryLogic
{
 public class Mod {public string Id=Guid.NewGuid().ToString(),TemplateId="fixture-template";public object Template=new();public SightComponent? Sight;public T? GetItemComponent<T>() where T:class=>Sight as T;}
 public class Iron:Mod{} public class Scope:Mod{} public class Dot:Mod{} public class ScopeTemplate{}
 public class SightComponent{}
 public class Weapon {public string ShortName="fixture rifle",WeapClass="assaultRifle";public bool BoltAction;public EFireMode[] WeapFireType=Array.Empty<EFireMode>();public IEnumerable<Mod> Mods=Array.Empty<Mod>();public enum EFireMode{fullauto}}
 public static class JsonTypes {public static Dictionary<string,Type> TypeTable=new(),TemplateTypeTable=new();}
 public static class Localization {public static string Localized(this string s)=>s;}
}
