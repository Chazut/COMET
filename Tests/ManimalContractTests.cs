using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

internal static class ManimalContractTests
{
 public static int Run(string path="C:/Games/SPT-4.1/BepInEx/plugins/CSGas/CSGasClient.dll")
 {
  if(!File.Exists(path)){Console.WriteLine("G08 optional installed-provider contract: skipped (mod absent).");return 0;}
  int n=0;void C(bool v,string why){n++;if(!v)throw new Exception("G08 installed provider: "+why);}
  using var stream=File.OpenRead(path);using var pe=new PEReader(stream);var md=pe.GetMetadataReader();var signatures=new Signatures();
  var version=md.GetAssemblyDefinition().Version;
  bool variants=version==new Version(2,0,1,0);
  C(variants||version==new Version(2,0,0,0),"audited assembly version");
  string expectedHash=variants?"586606FDB8EA3E42974C0AFFB184C17BC320C0873E4A0E7271754575A22DCBBE":"851589BF528A27CEEF9D079B832892335DDD395091C311880466B93D5775DD71";
  var sha=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
  C(sha==expectedHash,"audited "+version+" build identity (re-audit if changed)");
  var types=md.TypeDefinitions.Select(md.GetTypeDefinition).Where(t=>!t.IsNested).ToDictionary(t=>md.GetString(t.Namespace)+"."+md.GetString(t.Name));
  TypeDefinition T(string name){C(types.TryGetValue(name,out var t),name);return t;}
  const string cloudName="Manimal.CSGas.Gas.CsGasCloud",emitterName="Manimal.CSGas.Gas.VolumetricGasEmitter",kindName="Manimal.CSGas.GasVariant";
  var cloud=T(cloudName);var emitter=T(emitterName);var plugin=T("Manimal.CSGas.Plugin");
  FieldDefinition F(TypeDefinition t,string name,string type,bool isStatic=false){
   var found=t.GetFields().Select(md.GetFieldDefinition).Where(f=>md.GetString(f.Name)==name).ToArray();
   C(found.Length==1,"field "+name);var f=found[0];C(f.DecodeSignature(signatures,(object?)null)==type&&((f.Attributes&FieldAttributes.Static)!=0)==isStatic,"field signature "+name);return f;
  }
  void M(TypeDefinition t,string name,string returns,bool isStatic,params string[] args){
   var found=t.GetMethods().Select(md.GetMethodDefinition).Where(m=>md.GetString(m.Name)==name).Where(m=>m.DecodeSignature(signatures,(object?)null).ParameterTypes.SequenceEqual(args)).ToArray();
   C(found.Length==1,"method "+name+"("+string.Join(",",args)+")");var m=found[0];C(m.DecodeSignature(signatures,(object?)null).ReturnType==returns&&((m.Attributes&MethodAttributes.Static)!=0)==isStatic,"method signature "+name);
  }
  F(cloud,"_exploded","System.Boolean");F(cloud,"_emitter",emitterName);F(cloud,"_smoke","SmokeGrenade");
  F(emitter,"_origin","UnityEngine.Vector3");F(emitter,"_finished","System.Boolean");F(emitter,"_emitUntil","System.Single");F(emitter,"_particleLifetime","System.Single");
  F(plugin,"GasPointRadiusM","BepInEx.Configuration.ConfigEntry`1<System.Single>",true);F(plugin,"GasRequiresLineOfSight","BepInEx.Configuration.ConfigEntry`1<System.Boolean>",true);
  M(cloud,"Start","System.Void",false);M(cloud,"GetProtection",cloudName+"+GasProtection",true,"EFT.Player");
  M(emitter,"IsInside","System.Boolean",false,"UnityEngine.Vector3","System.Single");
  M(emitter,"Create",emitterName,true,variants?["SmokeGrenade","UnityEngine.GameObject","UnityEngine.Color"]:["SmokeGrenade","UnityEngine.GameObject"]);
  var protection=cloud.GetNestedTypes().Select(md.GetTypeDefinition).Single(t=>md.GetString(t.Name)=="GasProtection");
  C(protection.GetFields().Select(md.GetFieldDefinition).Select(f=>md.GetString(f.Name)).Where(x=>x!="value__").SequenceEqual(new[]{"None","Respirator","Full"}),"mask protection values");
  var attribute=plugin.GetCustomAttributes().Select(md.GetCustomAttribute).Single(a=>a.Constructor.Kind==HandleKind.MemberReference&&
   md.GetMemberReference((MemberReferenceHandle)a.Constructor).Parent.Kind==HandleKind.TypeReference&&
   signatures.GetTypeFromReference(md,(TypeReferenceHandle)md.GetMemberReference((MemberReferenceHandle)a.Constructor).Parent,0)=="BepInEx.BepInPlugin");
  var data=md.GetBlobReader(attribute.Value);C(data.ReadUInt16()==1&&data.ReadSerializedString()=="com.manimal.csgas"&&data.ReadSerializedString()=="Manimal-CSGas"&&data.ReadSerializedString()==version.ToString(3),"BepInEx GUID/name/version");
  if(variants){
   var kind=T(kindName);F(cloud,"Variant",kindName);
   foreach(var name in new[]{"Cs","Rda68","Rda78"})F(kind,name,kindName,true);
   var constants=T("Manimal.CSGas.CSGasConstants");
   foreach(var pair in new[]{("CsGasTpl","6a5d6a5f4ed8c025a0a2cff0"),("Rda68Tpl","6a9854e0788619fe8502a6b7"),("Rda78Tpl","6a9854955358fa10380b8509")}){
    var f=F(constants,pair.Item1,"System.String",true);var constant=md.GetConstant(f.GetDefaultValue());var bytes=md.GetBlobBytes(constant.Value);
    C(System.Text.Encoding.Unicode.GetString(bytes)==pair.Item2,"audited template "+pair.Item1);
   }
  }
  Console.WriteLine($"G08 installed Manimal {version} binary contract: {n} checks; SHA256 {sha}. Metadata only; no game assembly executed.");return n;
 }
 private sealed class Signatures:ISignatureTypeProvider<string,object?>
 {
  public string GetPrimitiveType(PrimitiveTypeCode c)=>c switch{PrimitiveTypeCode.Void=>"System.Void",PrimitiveTypeCode.Boolean=>"System.Boolean",PrimitiveTypeCode.Single=>"System.Single",PrimitiveTypeCode.String=>"System.String",_=>"System."+c};
  public string GetTypeFromDefinition(MetadataReader r,TypeDefinitionHandle h,byte k){var t=r.GetTypeDefinition(h);var name=r.GetString(t.Name);return t.IsNested?GetTypeFromDefinition(r,t.GetDeclaringType(),k)+"+"+name:Qualified(r.GetString(t.Namespace),name);}
  public string GetTypeFromReference(MetadataReader r,TypeReferenceHandle h,byte k){var t=r.GetTypeReference(h);return Qualified(r.GetString(t.Namespace),r.GetString(t.Name));}
  private static string Qualified(string ns,string name)=>ns.Length==0?name:ns+"."+name;
  public string GetTypeFromSpecification(MetadataReader r,object? c,TypeSpecificationHandle h,byte k)=>r.GetTypeSpecification(h).DecodeSignature(this,c);
  public string GetGenericInstantiation(string t,ImmutableArray<string> a)=>t+"<"+string.Join(",",a)+">";
  public string GetArrayType(string t,ArrayShape s)=>t+"[]";
  public string GetSZArrayType(string t)=>t+"[]";
  public string GetByReferenceType(string t)=>t+"&";
  public string GetPointerType(string t)=>t+"*";
  public string GetPinnedType(string t)=>t;
  public string GetModifiedType(string m,string t,bool r)=>t;
  public string GetGenericMethodParameter(object? c,int i)=>"!!"+i;
  public string GetGenericTypeParameter(object? c,int i)=>"!"+i;
  public string GetFunctionPointerType(MethodSignature<string> s)=>"fn";
 }
}
