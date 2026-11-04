using System;
using System.Collections.Generic;
using Comet.Core;
using EFT;
using EFT.InventoryLogic;
using Newtonsoft.Json;
using SAIN.Components;
using UnityEngine;

namespace Comet.Client;

internal sealed class GrenadeFilterRuntime
{
    internal GrenadeFilterTrace? Trace;
    internal string LogKey="",BoundItem="";
    internal double LogAt=-1000;
    internal bool Fault;
}
public sealed partial class Plugin
{
    private bool GrenadeFilterScope(BotComponent b)=>config?.Enabled==true&&config.GrenadeFilter.Enabled&&NativeAdapter.IsPmcAi(b)
        &&b.BotActive&&!b.GameEnding&&Authority&&!Ghost(b.ProfileId);
    private static string GrenadeCategory(ThrowWeap? item)=>item==null?"unknown":GrenadeFilterRules.Classify(item.ThrowType.ToString(),item.IsDummy,
        item.EmitTime,item.MaxExplosionDistance,item.Blindness.y,item.Contusion.y,item.FragmentsCount);
    private string FilterReject(BotComponent b,ThrowWeap? item)=>GrenadeFilterScope(b)
        ?GrenadeFilterRules.Reject(GrenadeCategory(item),config!.GrenadeFilter.AllowFlash):"";
    private bool PrepareOffensiveGrenade(BotComponent b,BotState s)
    {
        if(!GrenadeFilterScope(b))return true;
        var r=s.GrenadeFilter;var g=b.BotOwner.WeaponManager.Grenades;
        if(r.Fault||s.GrenadeJudgment.Fault)return false;
        if(g.ThrowindNow)return false; // Never switch or cancel hands mid-animation.
        bool first=r.Trace==null||r.Trace.State=="ended";
        r.BoundItem="";r.Trace=new(){Previous=g.grenade?.Id.ToString()??""};
        var items=new Dictionary<string,ThrowWeap>();ThrowWeap? best=null;int count=0;
        foreach(var carried in b.Player.Inventory.GetPlayerItems(EPlayerItems.Equipment))
        {
            if(++count>256)return BlockGrenadeFilter(s,"filter-inventory-limit");
            if(carried is not ThrowWeap item)continue;
            if(items.Count>=32)return BlockGrenadeFilter(s,"filter-grenade-limit");
            string id=item.Id.ToString();if(id.Length==0||items.ContainsKey(id))return BlockGrenadeFilter(s,"filter-ambiguous-item");items.Add(id,item);
            string category=GrenadeCategory(item),why=FilterReject(b,item);
            r.Trace.Alternatives.Add(new(){Item=id,Template=item.TemplateId.ToString(),Name=item.ShortName.Localized(),Category=category,Eligible=why.Length==0,Reason=why.Length==0?"eligible":why});
            if(why.Length>0)continue;
            // Retain an allowed current item; otherwise frag before flash, then stable item ID.
            if(best==null||ReferenceEquals(item,g.grenade)||!ReferenceEquals(best,g.grenade)
                &&(category=="fragmentation"&&GrenadeCategory(best)!="fragmentation"
                ||category==GrenadeCategory(best)&&string.CompareOrdinal(id,best.Id.ToString())<0))best=item;
        }
        if(best==null){DescribeFilterItem(r.Trace,g.grenade);return BlockGrenadeFilter(s,"filter-no-offensive-item");}
        if(!ReferenceEquals(best,g.grenade))
        {
            g.SetThrowParams(best);
            // The next search must calculate an arc for this item/power, never reuse the previous one.
            s.GrenadeJudgment.Arc=false;StopGrenadeTrajectory(s,"trajectory-filter-item-changed");StopGrenadeChoice(s,"choice-filter-item-changed");
            if(g.AIGreanageThrowData!=null)g.AIGreanageThrowData.ThrowComplete=true;
        }
        DescribeFilterItem(r.Trace,best);r.Trace.State="selected";
        r.Trace.Reason=r.Trace.Previous==r.Trace.Item?"filter-offensive-item-retained":"filter-offensive-item-replaced";
        if(first||r.Trace.Previous!=r.Trace.Item)RecordGrenadeFilter(s);return true;
    }
    private static void DescribeFilterItem(GrenadeFilterTrace t,ThrowWeap? item)
    {t.Item=item?.Id.ToString()??"";t.Template=item?.TemplateId.ToString()??"";t.Name=item?.ShortName.Localized()??"";t.Category=GrenadeCategory(item);}
    private bool BindOffensiveGrenade(BotComponent b,BotState s,bool arc)
    {
        if(!GrenadeFilterScope(b))return arc;
        if(!arc)return false;
        var r=s.GrenadeFilter;var item=b.BotOwner.WeaponManager.Grenades.grenade;
        string why=FilterReject(b,item);if(why.Length>0)return BlockGrenadeFilter(s,why);
        r.Trace??=new();DescribeFilterItem(r.Trace,item);r.BoundItem=r.Trace.Item;
        r.Trace.State="selected";r.Trace.ValidFor=.75;r.Trace.Reason="filter-prepared-item";RecordGrenadeFilter(s);return true;
    }
    private bool ValidateOffensiveGrenade(BotComponent b,BotState s)
    {
        if(!GrenadeFilterScope(b))return true;
        var r=s.GrenadeFilter;var g=b.BotOwner.WeaponManager.Grenades;var item=g.grenade;
        if(r.Fault||g.ThrowindNow)return false;
        string why=FilterReject(b,item);
        if(why.Length==0&&(item==null||r.BoundItem.Length==0||item.Id.ToString()!=r.BoundItem))why="filter-prepared-item-changed";
        bool carried=false;int count=0;var ids=new HashSet<string>();
        if(why.Length==0)foreach(var inv in b.Player.Inventory.GetPlayerItems(EPlayerItems.Equipment))
        {
            if(++count>256){why="filter-inventory-limit";break;}
            if(inv is ThrowWeap grenade){if(!ids.Add(grenade.Id.ToString())){why="filter-ambiguous-item";break;}if(ReferenceEquals(inv,item))carried=true;}
        }
        if(why.Length==0&&!carried)why="filter-item-no-longer-carried";
        if(why.Length==0&&g.AIGreanageThrowData?.GrenadeType is { } type&&type.ToString()!=item!.ThrowType.ToString())why="filter-arc-type-changed";
        if(why.Length>0){r.Trace??=new();DescribeFilterItem(r.Trace,item);return BlockGrenadeFilter(s,why);}
        // Prevent native DoThrow's optional type selector from replacing the validated actual item.
        if(g.AIGreanageThrowData!=null)g.AIGreanageThrowData.GrenadeType=null;
        r.Trace??=new();DescribeFilterItem(r.Trace,item);r.Trace.State="validated";r.Trace.ValidFor=.75;r.Trace.Reason="filter-execution-validated";RecordGrenadeFilter(s);return true;
    }
    private bool BlockGrenadeFilter(BotState s,string why)
    {var r=s.GrenadeFilter;r.BoundItem="";r.Trace??=new();r.Trace.State="blocked";r.Trace.ValidFor=.75;r.Trace.Reason=why;RecordGrenadeFilter(s);return false;}
    private void EndGrenadeFilter(BotState s,string why)
    {var r=s.GrenadeFilter;r.BoundItem="";if(r.Trace==null)return;r.Trace.State="ended";r.Trace.Reason=why;r.Trace.ValidFor=0;RecordGrenadeFilter(s);}
    private void FilterError(BotState s,Exception ex)
    {if(!s.GrenadeFilter.Fault)Logger.LogWarning("G07 blocked grenade requests: "+ex.GetBaseException().Message);s.GrenadeFilter.Fault=true;BlockGrenadeFilter(s,"filter-adapter-error");}
    private void RecordGrenadeFilter(BotState s)
    {
        var r=s.GrenadeFilter;var t=r.Trace;if(t==null)return;
        string key=t.State+"|"+t.Reason+"|"+t.Item+"|"+t.Category;
        if(key==r.LogKey&&Time.timeAsDouble-r.LogAt<.75)return;r.LogKey=key;r.LogAt=Time.timeAsDouble;
        var json=JsonConvert.SerializeObject(new{schema=1,session,sequence=++sequence,feature="G07",stage="evaluated",decision="GrenadeFilter",reason=t.Reason,
            realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),configRevision=config?.Revision??0,profileId=s.ProfileId,name=s.Name,botInstance=s.Id,
            grenadeFilter=t,grenadeFilterConfig=config?.GrenadeFilter},Diagnostics.Json);
        diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>{if(!subscriberWarned){subscriberWarned=true;Logger.LogWarning(ex.Message);}});
    }
}
