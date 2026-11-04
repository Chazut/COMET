using System;
using System.Collections.Generic;

namespace Comet.Core;

public sealed class GrenadeFilterConfig
{
    public bool Enabled { get; set; } = true;
    public bool AllowFlash { get; set; } = true;
}
public sealed class GrenadeFilterAlternative
{
    public string Item="",Template="",Name="",Category="unknown",Reason="";
    public bool Eligible;
}
public sealed class GrenadeFilterTrace
{
    public string State="blocked",Reason="filter-no-offensive-item",Item="",Template="",Name="",Category="unknown",Previous="";
    public double ValidFor=.75;
    public List<GrenadeFilterAlternative> Alternatives=new();
}
public static class GrenadeFilterRules
{
    public static string Classify(string type,bool dummy,double emit,double blast,double blind,double contusion,int fragments)
    {
        // Type precedes IsDummy: vanilla smoke has no blast/blindness and is also a native dummy.
        if(type=="smoke_grenade")return "smoke";
        if(type=="gas_grenade")return "gas";
        if(dummy)return "dummy";
        if(!Finite(emit)||!Finite(blast)||!Finite(blind)||!Finite(contusion)||emit<0||blast<0||blind<0||contusion<0||fragments<0)return "unknown";
        if(emit>0)return "special"; // Persistent effects must not inherit frag/flash permission.
        if(type=="frag_grenade"&&blast>0)return "fragmentation";
        if(type=="flash_grenade"&&blind>0&&blast==0&&fragments==0)return "flash";
        return "unknown";
    }
    public static string Reject(string category,bool allowFlash)=>category switch
    {
        "fragmentation"=>"", "flash"=>allowFlash?"":"filter-flash-disabled",
        "smoke"=>"filter-smoke-not-offensive", "gas"=>"filter-gas-not-offensive",
        "dummy"=>"filter-dummy-item", "special"=>"filter-special-effect", _=>"filter-unknown-effect"
    };
    public static string FlashReject(double fuse,double contact,double flight,double margin)
    {
        if(!Finite(contact)||contact>=0)return "contact-explosion";
        if(!Finite(fuse)||fuse<.75||fuse>30)return "flash-fuse-outside-safe-range";
        if(!Finite(flight)||flight<=0||!Finite(margin)||fuse-flight<margin)return "insufficient-arrival-margin";
        return "";
    }
    private static bool Finite(double v)=>!double.IsNaN(v)&&!double.IsInfinity(v);
}
