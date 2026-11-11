using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using EFT;
using HarmonyLib;
using UnityEngine;
namespace Comet.Client;
// Optional, version-pinned read-only integration. No copied gas simulation or hard DLL reference.
internal sealed class ManimalGasBridge
{
 internal const string Guid="com.manimal.csgas";
 private Type cloud=null!,emitter=null!;
 private FieldInfo exploded=null!,cloudEmitter=null!,smoke=null!,origin=null!,finished=null!,until=null!,lifetime=null!,pointRadius=null!,los=null!;
 private MethodInfo contains=null!,protection=null!,start=null!,create=null!;
 private FieldInfo? variantField;
 private object? csVariant,rda68Variant,rda78Variant;
 private readonly struct TrackedComponent
 {
  private readonly WeakReference<Component> reference;
  internal readonly int Id;
  internal TrackedComponent(Component c){reference=new(c);Id=c.GetInstanceID();}
  internal bool TryGetTarget(out Component? c)=>reference.TryGetTarget(out c);
 }
 private readonly List<TrackedComponent> clouds=new(),emitters=new();
 // Scalar labels survive a canister disappearing; components remain weakly referenced.
 private readonly Dictionary<int,string> emitterVariants=new();
 internal bool Ready;
 internal string Status="Manimal-CSGas absent";
 internal string Provider="Manimal-CSGas";
 internal bool Bind(Assembly assembly,System.Version version)
 {
  Ready=false;Clear();variantField=null;csVariant=rda68Variant=rda78Variant=null;
  Provider="Manimal-CSGas "+version;
  bool variants=version==new System.Version(2,0,1);
  if(!variants&&version!=new System.Version(2,0,0)){Status="Unsupported Manimal-CSGas version "+version;return false;}
  try{
   cloud=assembly.GetType("Manimal.CSGas.Gas.CsGasCloud",true)!;emitter=assembly.GetType("Manimal.CSGas.Gas.VolumetricGasEmitter",true)!;
   var plugin=assembly.GetType("Manimal.CSGas.Plugin",true)!;
   FieldInfo F(Type t,string name,Type expected,bool isStatic=false){var f=AccessTools.Field(t,name);if(f==null||f.FieldType!=expected||f.IsStatic!=isStatic)throw new MissingMemberException(t.FullName+"."+name);return f;}
   exploded=F(cloud,"_exploded",typeof(bool));cloudEmitter=F(cloud,"_emitter",emitter);smoke=F(cloud,"_smoke",typeof(SmokeGrenade));
   origin=F(emitter,"_origin",typeof(Vector3));finished=F(emitter,"_finished",typeof(bool));until=F(emitter,"_emitUntil",typeof(float));lifetime=F(emitter,"_particleLifetime",typeof(float));
   pointRadius=F(plugin,"GasPointRadiusM",typeof(ConfigEntry<float>),true);los=F(plugin,"GasRequiresLineOfSight",typeof(ConfigEntry<bool>),true);
   if(variants){
    var kind=assembly.GetType("Manimal.CSGas.GasVariant",true)!;
    variantField=F(cloud,"Variant",kind);
    csVariant=F(kind,"Cs",kind,true).GetValue(null);rda68Variant=F(kind,"Rda68",kind,true).GetValue(null);rda78Variant=F(kind,"Rda78",kind,true).GetValue(null);
    if(csVariant==null||rda68Variant==null||rda78Variant==null||ReferenceEquals(csVariant,rda68Variant)||ReferenceEquals(csVariant,rda78Variant)||ReferenceEquals(rda68Variant,rda78Variant))throw new MissingMemberException("gas variants");
   }
   contains=AccessTools.Method(emitter,"IsInside",new[]{typeof(Vector3),typeof(float)})??throw new MissingMethodException("gas IsInside");
   protection=AccessTools.Method(cloud,"GetProtection",new[]{typeof(Player)})??throw new MissingMethodException("gas protection");
   start=AccessTools.Method(cloud,"Start",Type.EmptyTypes)??throw new MissingMethodException("gas Start");
   create=AccessTools.Method(emitter,"Create",variants?new[]{typeof(SmokeGrenade),typeof(GameObject),typeof(Color)}:new[]{typeof(SmokeGrenade),typeof(GameObject)})??throw new MissingMethodException("gas Create");
   if(contains.IsStatic||contains.ReturnType!=typeof(bool)||!protection.IsStatic||!protection.ReturnType.IsEnum||string.Join(",",Enum.GetNames(protection.ReturnType))!="None,Respirator,Full"
    ||start.IsStatic||start.ReturnType!=typeof(void)||!create.IsStatic||create.ReturnType!=emitter)throw new MissingMemberException("gas contract");
   Ready=true;Status=Provider+" compatible"+(variants?" (CS, RDA-68, RDA-78)":" (CS)");return true;
  }catch(Exception e){Ready=false;Status="Manimal-CSGas contract unavailable: "+e.GetBaseException().Message;return false;}
 }
 internal bool Discover()
 {
  try{
   if(!Chainloader.PluginInfos.TryGetValue(Guid,out var p)){Ready=false;Clear();Status="Manimal-CSGas absent";return false;}
   if(p.Instance==null){Ready=false;Status="Manimal-CSGas did not load";return false;}
   return Bind(p.Instance.GetType().Assembly,p.Metadata.Version);
  }catch(Exception e){Ready=false;Status="Manimal-CSGas unavailable: "+e.GetBaseException().Message;return false;}
 }
 internal void Hook(Harmony h)
 {
  h.Patch(start,postfix:new HarmonyMethod(typeof(Plugin),nameof(Plugin.GasCloudCreated)));
  h.Patch(create,postfix:new HarmonyMethod(typeof(Plugin),nameof(Plugin.GasEmitterCreated)));
 }
 internal void Add(Component c,bool volume,SmokeGrenade? source=null)
 {
  if(!Ready||c==null)return;var list=volume?emitters:clouds;
  Prune();
  foreach(var w in list)if(w.TryGetTarget(out var v)&&ReferenceEquals(v,c))return;
  if(list.Count>=64)throw new InvalidOperationException("gas cloud safety limit");
  list.Add(new(c));
  if(volume){
   string label=variantField==null?"CS":"Unknown";
   if(source!=null)foreach(var w in clouds)if(w.TryGetTarget(out var v)&&v!=null&&ReferenceEquals(smoke.GetValue(v),source)){label=Variant(v);break;}
   emitterVariants[c.GetInstanceID()]=label;
  }
 }
 private string Variant(Component c)
 {
  if(variantField==null)return "CS";
  var kind=variantField.GetValue(c);
  return ReferenceEquals(kind,csVariant)?"CS":ReferenceEquals(kind,rda68Variant)?"RDA-68":ReferenceEquals(kind,rda78Variant)?"RDA-78":"Unknown";
 }
 private void Prune()
 {
  // At most 64 entries; keep label storage bounded even when grenades are destroyed between samples.
  for(int i=emitters.Count-1;i>=0;i--)if(!emitters[i].TryGetTarget(out var c)||c==null){emitterVariants.Remove(emitters[i].Id);emitters.RemoveAt(i);}
  clouds.RemoveAll(w=>!w.TryGetTarget(out var c)||c==null);
 }
 internal string Protection(Player p)=>protection.Invoke(null,new object[]{p})?.ToString()??throw new InvalidOperationException("missing gas protection");
 internal bool Inside(Vector3 head,out string id,out Vector3 center,out int active)
  =>Inside(head,out id,out center,out active,out _);
 internal bool Inside(Vector3 head,out string id,out Vector3 center,out int active,out string variant)
 {
  id="";variant="";center=default;active=0;bool found=false;
  if(!Ready)return false;
  Prune();
  float radius=((ConfigEntry<float>)pointRadius.GetValue(null)!).Value;
  if(float.IsNaN(radius)||float.IsInfinity(radius)||radius<=0||radius>5)throw new InvalidOperationException("invalid gas radius");
  foreach(var w in emitters)if(w.TryGetTarget(out var c)&&c!=null&&c.gameObject.activeInHierarchy)
  {
   if((bool)finished.GetValue(c)!&&Time.time>=(float)until.GetValue(c)!+(float)lifetime.GetValue(c)!)continue;
   active++;
   if((bool)contains.Invoke(c,new object[]{head,radius})!){found=true;id="volume:"+c.GetInstanceID();center=(Vector3)origin.GetValue(c)!;variant=emitterVariants.TryGetValue(c.GetInstanceID(),out var label)?label:"Unknown";}
  }
  foreach(var w in clouds)if(w.TryGetTarget(out var c)&&c!=null&&c.gameObject.activeInHierarchy&&(bool)exploded.GetValue(c)!)
  {
   // A live emitter is authoritative, including its lingering cloud after the canister disappears.
   if(cloudEmitter.GetValue(c) is Component e&&e!=null)continue;
   var grenade=smoke.GetValue(c) as SmokeGrenade;if(grenade==null)continue;
   var area=grenade._smokeGrenadeSettings?._emissionArea;if(area==null||!area.gameObject.activeInHierarchy)continue;
   active++;var scale=area.transform.lossyScale;float size=area.radius*Mathf.Max(scale.x,Mathf.Max(scale.y,scale.z));var pos=area.transform.position;
   if((head-pos).sqrMagnitude>size*size)continue;
   if(((ConfigEntry<bool>)los.GetValue(null)!).Value&&Physics.Linecast(pos,head,LayersMaskController.GrenadeObstaclesColliderMask))continue;
   found=true;id="sphere:"+c.GetInstanceID();center=pos;variant=Variant(c);
  }
  return found;
 }
 internal bool Owns(Grenade grenade)
 {
  if(!Ready||grenade==null)return false;
  foreach(var w in clouds)if(w.TryGetTarget(out var c)&&c!=null&&ReferenceEquals(smoke.GetValue(c),grenade))return true;
  return false;
 }
 internal void Clear(){clouds.Clear();emitters.Clear();emitterVariants.Clear();}
}
