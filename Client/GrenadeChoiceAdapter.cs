using System;
using System.Collections.Generic;
using Comet.Core;
using EFT;
using EFT.InventoryLogic;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;

namespace Comet.Client;

internal sealed class GrenadeChoiceRuntime
{
    internal GrenadeChoiceTrace? Trace;
    internal string Item="",LogKey="";
    internal int Enemy;
    internal long Revision;
    internal double ChosenAt,Power,LogAt=-1000;
    internal Vector3 Known;
    // No item, controller, enemy or GameWorld references survive raid teardown.
}
public sealed partial class Plugin
{
    private bool GrenadeChoiceScope(BotComponent b)=>config?.Enabled==true&&config.GrenadeChoice.Enabled
        &&NativeAdapter.IsSupportedAi(b)&&b.BotActive&&!b.GameEnding&&Authority&&!Ghost(b.ProfileId);
    private bool GrenadeChoiceInProgress(BotComponent b)=>GrenadeChoiceScope(b)&&b.BotOwner.WeaponManager.Grenades.ThrowindNow;

    private bool ChooseGrenade(BotComponent b,Enemy e,BotState s,bool arc)
    {
        if(!GrenadeChoiceScope(b))return arc;
        var r=s.GrenadeChoice;var g=b.BotOwner.WeaponManager.Grenades;var f=GrenadeFrame(b,e,s);
        if(!arc){StopGrenadeChoice(s,"choice-no-trajectory","blocked");return false;}
        if(g.ThrowindNow){StopGrenadeChoice(s,"choice-native-throw-in-progress","blocked");return false;}
        string evidence=ChoiceEvidence(b,e,f);
        if(evidence.Length>0){StopGrenadeChoice(s,evidence,"blocked");return false;}
        var d=g.AIGreanageThrowData;
        if(d==null||!ValidGrenadeArc(d)||!d.IsUpToDate()||d.ThrowComplete){StopGrenadeChoice(s,"choice-arc-expired","blocked");return false;}
        if(r.Item.Length>0&&r.Enemy==f.Instance&&r.Revision==config!.Revision&&f.Now-r.ChosenAt<=2
            &&Vector3.Distance(r.Known,new((float)f.X,(float)f.Y,(float)f.Z))<=1)
            return ValidateGrenadeChoice(b,e,s,false); // Same opportunity keeps the same item, even with a new arc.

        var flat=d.Target-d.Position;flat.y=0;
        var flight=GrenadeChoiceRules.Flight(flat.magnitude,d.Force,d.Ang);
        bool indoors=b.Player.Environment==EnvironmentType.Indoor||f.Room;
        double distance=Vector3.Distance(d.Position,d.Target);
        bool shortest=config!.GrenadeChoice.PreferShortIndoors&&indoors||distance<config.GrenadeChoice.ShortFuseDistance;
        r.Trace=new(){Target=f.Target,Source=f.Source,KnownX=f.X,KnownY=f.Y,KnownZ=f.Z,Distance=distance,
            Flight=flight??0,RequiredMargin=config.GrenadeChoice.ArrivalMargin,
            Preference=shortest?(config.GrenadeChoice.PreferShortIndoors&&indoors?"short-indoors":"short-close"):"long-outdoors"};
        r.Item="";
        if(!flight.HasValue){StopGrenadeChoice(s,"choice-unknown-flight","blocked");return false;}
        var items=new Dictionary<string,ThrowWeap>();int count=0;
        foreach(var carried in b.Player.Inventory.GetPlayerItems(EPlayerItems.Equipment))
        {
            if(++count>256){StopGrenadeChoice(s,"choice-inventory-limit","blocked");return false;}
            if(carried is not ThrowWeap item)continue;
            if(items.Count>=32){StopGrenadeChoice(s,"choice-grenade-limit","blocked");return false;}
            string id=item.Id.ToString();
            if(id.Length==0||items.ContainsKey(id)){StopGrenadeChoice(s,"choice-ambiguous-item","blocked");return false;}
            items.Add(id,item);
            string why=ChoiceReject(b,item,flight.Value);
            r.Trace.Alternatives.Add(new(){Item=id,Template=item.TemplateId.ToString(),Name=item.ShortName.Localized(),Fuse=GrenadeChoiceRules.Finite(item.GetExplDelay)?item.GetExplDelay:null,
                Category=GrenadeCategory(item),Eligible=why.Length==0,Reason=why.Length==0?"eligible":why});
        }
        var fragments=r.Trace.Alternatives.FindAll(a=>a.Category=="fragmentation"||!GrenadeFilterScope(b)&&items[a.Item].ThrowType.ToString()=="frag_grenade");
        var pick=GrenadeChoiceRules.Choose(fragments,shortest,f.Item);
        if(pick==null&&GrenadeFilterScope(b)&&config.GrenadeFilter.AllowFlash)
        {
            foreach(var a in r.Trace.Alternatives)if(a.Category=="flash"&&a.Eligible
                &&(pick==null||a.Item==f.Item||pick.Item!=f.Item&&string.CompareOrdinal(a.Item,pick.Item)<0))pick=a;
            if(pick!=null)r.Trace.Preference="flash-fallback";
        }
        if(pick==null){StopGrenadeChoice(s,"choice-no-safe-item","blocked");return false;}
        var chosen=items[pick.Item];float oldPower=g.MaxPower;
        g.SetThrowParams(chosen); // Native inventory/animation/Fika execution stays intact.
        r.Item=pick.Item;r.Enemy=f.Instance;r.Revision=config.Revision;r.Known=new((float)f.X,(float)f.Y,(float)f.Z);r.ChosenAt=f.Now;r.Power=g.MaxPower;
        r.Trace.Item=pick.Item;r.Trace.Template=pick.Template;r.Trace.Name=pick.Name;r.Trace.Fuse=pick.Fuse!.Value;r.Trace.Margin=pick.Fuse.Value-flight.Value;
        foreach(var a in r.Trace.Alternatives)if(a.Eligible)a.Reason=a.Item==pick.Item?"selected":a.Category=="flash"?"flash-reserve":"less-preferred-fuse";
        // Fixed native mass currently makes power identical. Fail closed if a future mod changes that contract.
        if(!GrenadeChoiceRules.Finite(g.MaxPower)||Math.Abs(g.MaxPower-oldPower)>.001)
        {FailGrenadeTrajectory(s,"trajectory-item-power-changed");StopGrenadeChoice(s,"choice-item-needs-new-arc","blocked");return false;}
        s.GrenadeJudgment.ArcItem=r.Item;
        if(GrenadeTrajectoryScope(b))s.GrenadeTrajectory.Item=r.Item;
        // Native DoThrow otherwise selects the first grenade of this type, silently discarding our choice.
        d.GrenadeType=null;
        if(!ValidateGrenadeChoice(b,e,s,false))return false;
        r.Trace.State="selected";r.Trace.Reason="choice-"+r.Trace.Preference;r.Trace.ValidFor=.75;RecordGrenadeChoice(s);return true;
    }
    private string ChoiceEvidence(BotComponent b,Enemy e,GrenadeJudgmentFrame f)
    {
        if(!ReferenceEquals(e,b.GoalEnemy)||!f.Personal||!new DoorPoint(f.X,f.Y,f.Z).Finite
            ||!GrenadeChoiceRules.Finite(f.EvidenceAt)||f.Now<f.EvidenceAt||f.Now-f.EvidenceAt>=config!.GrenadeContactLifetime)return "choice-no-fresh-personal-contact";
        return f.Block;
    }
    private string ChoiceReject(BotComponent b,ThrowWeap item,double flight)
    {
        string filtered=FilterReject(b,item);if(filtered.Length>0)return filtered;
        if(GrenadeFilterScope(b)&&GrenadeCategory(item)=="flash")
            return GrenadeFilterRules.FlashReject(item.GetExplDelay,item.MinTimeToContactExplode,flight,config!.GrenadeChoice.ArrivalMargin);
        return GrenadeChoiceRules.Reject(item.GetExplDelay,item.MinTimeToContactExplode,item.IsDummy,item.ThrowType.ToString()=="frag_grenade",flight,config!.GrenadeChoice);
    }
    private bool ValidateGrenadeChoice(BotComponent b,Enemy e,BotState s,bool execution)
    {
        if(!GrenadeChoiceScope(b))return true;
        var r=s.GrenadeChoice;var g=b.BotOwner.WeaponManager.Grenades;var f=GrenadeFrame(b,e,s);var d=g.AIGreanageThrowData;
        string why=ChoiceEvidence(b,e,f);
        if(why.Length==0&&(g.ThrowindNow||r.Item.Length==0||r.Item!=f.Item||r.Enemy!=f.Instance||r.Revision!=config!.Revision
            ||f.Now<r.ChosenAt||f.Now-r.ChosenAt>2||Vector3.Distance(r.Known,new((float)f.X,(float)f.Y,(float)f.Z))>1))why="choice-binding-changed";
        if(why.Length==0&&(d==null||!ValidGrenadeArc(d)||!d.IsUpToDate()||d.ThrowComplete))why="choice-arc-expired";
        if(why.Length==0&&(!GrenadeChoiceRules.Finite(g.MaxPower)||Math.Abs(g.MaxPower-r.Power)>.001))why="choice-throw-power-changed";
        double? flight=null;
        if(why.Length==0)
        {
            var flat=d!.Target-d.Position;flat.y=0;flight=GrenadeChoiceRules.Flight(flat.magnitude,d.Force,d.Ang);
            if(!flight.HasValue)why="choice-unknown-flight";
        }
        if(why.Length==0)
        {
            bool carried=false;int count=0;
            foreach(var item in b.Player.Inventory.GetPlayerItems(EPlayerItems.Equipment))
            {if(++count>256){why="choice-inventory-limit";break;}if(ReferenceEquals(item,g.grenade))carried=true;}
            if(why.Length==0)why=!carried?"choice-item-no-longer-carried":ChoiceReject(b,g.grenade!,flight!.Value);
        }
        if(why.Length==0)why=GrenadeSafety(b,s,f);
        if(why.Length==0&&!NativeAdapter.GrenadeFriendsClear(b,d!.Target))why="sain-friendly-distance";
        // With G02 disabled, retain its bounded native-arc clearance guard at execution.
        if(why.Length==0&&execution&&!GrenadeTrajectoryScope(b))
        {
            if(!grenadeArcBudget.TryBegin(Time.frameCount))return false;
            grenadeArcClock.Restart();
            try{why=SweepGrenadeArc(d!,out _);}
            finally{grenadeArcClock.Stop();grenadeArcBudget.Charge(grenadeArcClock.Elapsed.TotalMilliseconds);}
        }
        if(why.Length>0){StopGrenadeChoice(s,why,"blocked");return false;}
        d!.GrenadeType=null;
        r.Trace!.Flight=flight!.Value;r.Trace.Fuse=g.grenade!.GetExplDelay;r.Trace.Margin=r.Trace.Fuse-flight.Value;
        if(execution){r.Trace.State="validated";r.Trace.Reason="choice-execution-validated";r.Trace.ValidFor=.75;RecordGrenadeChoice(s,true);}
        return true;
    }
    private void StopGrenadeChoice(BotState s,string reason,string state="ended")
    {var r=s.GrenadeChoice;r.Item="";if(r.Trace==null)return;r.Trace.State=state;r.Trace.Reason=reason;r.Trace.ValidFor=state=="ended"?0:.75;RecordGrenadeChoice(s);}
    private void RecordGrenadeChoice(BotState s,bool force=false)
    {
        var r=s.GrenadeChoice;var t=r.Trace;if(t==null)return;
        string key=t.State+"|"+t.Reason+"|"+t.Item;
        if(!force&&key==r.LogKey&&Time.timeAsDouble-r.LogAt<.75)return;r.LogKey=key;r.LogAt=Time.timeAsDouble;
        var json=Diagnostics.Serialize(new{schema=1,session,sequence=++sequence,feature="G03",stage="evaluated",decision="GrenadeChoice",reason=t.Reason,
            realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),configRevision=config?.Revision??0,profileId=s.ProfileId,name=s.Name,botInstance=s.Id,
            grenadeChoice=t,grenadeChoiceConfig=config?.GrenadeChoice});
        diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>{if(!subscriberWarned){subscriberWarned=true;Logger.LogWarning(ex.Message);}});
    }
}
