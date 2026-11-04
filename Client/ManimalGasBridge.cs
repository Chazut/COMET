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
 private MethodInfo contains=null!,protection=null!;
 private readonly List<WeakReference<Component>> clouds=new(),emitters=new();
 internal bool Ready;
 internal string Status="Manimal-CSGas absent";
 internal bool Bind(Assembly assembly,System.Version version)
 {
  if(version!=new System.Version(2,0,0)){Status="Unsupported Manimal-CSGas version "+version;return false;}
  try{
   cloud=assembly.GetType("Manimal.CSGas.Gas.CsGasCloud",true)!;emitter=assembly.GetType("Manimal.CSGas.Gas.VolumetricGasEmitter",true)!;
   var plugin=assembly.GetType("Manimal.CSGas.Plugin",true)!;
   FieldInfo F(Type t,string name,Type expected){var f=AccessTools.Field(t,name);if(f==null||f.FieldType!=expected)throw new MissingMemberException(t.FullName+"."+name);return f;}
   exploded=F(cloud,"_exploded",typeof(bool));cloudEmitter=F(cloud,"_emitter",emitter);smoke=F(cloud,"_smoke",typeof(SmokeGrenade));
   origin=F(emitter,"_origin",typeof(Vector3));finished=F(emitter,"_finished",typeof(bool));until=F(emitter,"_emitUntil",typeof(float));lifetime=F(emitter,"_particleLifetime",typeof(float));
   pointRadius=F(plugin,"GasPointRadiusM",typeof(ConfigEntry<float>));los=F(plugin,"GasRequiresLineOfSight",typeof(ConfigEntry<bool>));
   contains=AccessTools.Method(emitter,"IsInside",new[]{typeof(Vector3),typeof(float)})??throw new MissingMethodException("gas IsInside");
   protection=AccessTools.Method(cloud,"GetProtection",new[]{typeof(Player)})??throw new MissingMethodException("gas protection");
   if(contains.ReturnType!=typeof(bool)||!protection.IsStatic||!protection.ReturnType.IsEnum||string.Join(",",Enum.GetNames(protection.ReturnType))!="None,Respirator,Full")throw new MissingMemberException("gas contract");
   Ready=true;Status="Manimal-CSGas 2.0.0 compatible";return true;
  }catch(Exception e){Ready=false;Status="Manimal-CSGas contract unavailable: "+e.GetBaseException().Message;return false;}
 }
 internal bool Discover()
 {
  try{
   if(!Chainloader.PluginInfos.TryGetValue(Guid,out var p))return false;
   if(p.Instance==null){Ready=false;Status="Manimal-CSGas did not load";return false;}
   return Bind(p.Instance.GetType().Assembly,p.Metadata.Version);
  }catch(Exception e){Ready=false;Status="Manimal-CSGas unavailable: "+e.GetBaseException().Message;return false;}
 }
 internal void Hook(Harmony h)
 {
  h.Patch(AccessTools.Method(cloud,"Start"),postfix:new HarmonyMethod(typeof(Plugin),nameof(Plugin.GasCloudCreated)));
  h.Patch(AccessTools.Method(emitter,"Create"),postfix:new HarmonyMethod(typeof(Plugin),nameof(Plugin.GasEmitterCreated)));
 }
 internal void Add(Component c,bool volume)
 {
  if(!Ready||c==null)return;var list=volume?emitters:clouds;
  list.RemoveAll(x=>!x.TryGetTarget(out var v)||v==null);
  foreach(var w in list)if(w.TryGetTarget(out var v)&&ReferenceEquals(v,c))return;
  if(list.Count>=64)throw new InvalidOperationException("gas cloud safety limit");
  list.Add(new(c));
 }
 internal string Protection(Player p)=>protection.Invoke(null,new object[]{p})?.ToString()??throw new InvalidOperationException("missing gas protection");
 internal bool Inside(Vector3 head,out string id,out Vector3 center,out int active)
 {
  id="";center=default;active=0;bool found=false;
  emitters.RemoveAll(w=>!w.TryGetTarget(out var c)||c==null);
  clouds.RemoveAll(w=>!w.TryGetTarget(out var c)||c==null);
  float radius=((ConfigEntry<float>)pointRadius.GetValue(null)!).Value;
  if(float.IsNaN(radius)||float.IsInfinity(radius)||radius<=0||radius>5)throw new InvalidOperationException("invalid gas radius");
  foreach(var w in emitters)if(w.TryGetTarget(out var c)&&c!=null&&c.gameObject.activeInHierarchy)
  {
   if((bool)finished.GetValue(c)!&&Time.time>=(float)until.GetValue(c)!+(float)lifetime.GetValue(c)!)continue;
   active++;
   if((bool)contains.Invoke(c,new object[]{head,radius})!){found=true;id="volume:"+c.GetInstanceID();center=(Vector3)origin.GetValue(c)!;}
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
   found=true;id="sphere:"+c.GetInstanceID();center=pos;
  }
  return found;
 }
 internal bool Owns(Grenade grenade)
 {
  if(!Ready||grenade==null)return false;
  foreach(var w in clouds)if(w.TryGetTarget(out var c)&&c!=null&&ReferenceEquals(smoke.GetValue(c),grenade))return true;
  return false;
 }
 internal void Clear(){clouds.Clear();emitters.Clear();}
}
