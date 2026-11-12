using System;
using System.Collections.Generic;
using Comet.Core;
using HarmonyLib;
using SAIN.Components;
using SAIN.Models.Enums;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.SubComponents.CoverFinder;
using UnityEngine;

namespace Comet.Client;

internal sealed class FlankRuntime
{
    internal CoverPoint? Point;
    internal readonly Dictionary<string,float> Refused = new();
    internal bool MoveAccepted;
    internal string Target="", Reason="";
    internal void Reset() { Point=null;MoveAccepted=false;Target="";Reason=""; }
    internal string Refuse(string reason)
    {
        Reason=reason;
        return reason;
    }
}

// Select the actual point we will pass to SAIN, not a second independent native search.
internal static class FlankAdapter
{
    internal static bool Arrived(BotComponent b,BotState s)
        => s.Flank.MoveAccepted && s.Flank.Point is CoverPoint p
            && (b.Position-p.Position).sqrMagnitude<=2.25f
            && (p.BotInThisCover || b.Mover.ActivePath?.Status==EBotMoveStatus.Complete
                && (b.Mover.ActivePath.Destination-p.Position).sqrMagnitude<=1);

    internal static string Capability(BotComponent b,BotState s)
    {
        var f=s.Flank;
        if(s.Engine.Current==Tactic.Flank && f.Target==b.GoalEnemy?.EnemyProfileId && f.Point!=null)
        {
            if(Arrived(b,s))return "flank-arrived";
            if(f.Reason.Length>0)return f.Reason;
            if(!VisibleAdapter.ValidCover(b,f.Point))return f.Refuse("flank-destination-invalid");
            // ShiftCoverComplete also means no point/path was found. It is never arrival evidence.
            if(f.MoveAccepted && b.Decision.CurrentCombatDecision==ECombatDecision.ShiftCover
                && b.Decision.EnemyDecisions.ShiftCoverComplete)return f.Refuse("flank-native-ended-before-arrival");
            return "";
        }
        f.Reset();
        var used=b.Cover.CoverInUse;
        if(used==null)return "no-native-shift-cover-target";
        CoverPoint? best=null;float length=float.MaxValue;
        foreach(var point in b.Cover.CoverPoints)
        {
            if(point==null||point==used||point.CoverHeight<=used.CoverHeight
                ||(point.Position-used.Position).sqrMagnitude<=5||(point.Position-b.Position).sqrMagnitude<=2.25f
                ||!VisibleAdapter.ValidCover(b,point))continue;
            string key=ProgressAdapter.CoverKey(point);
            if(point.PathData.PathLength>=length)continue;
            best=point;length=point.PathData.PathLength;
        }
        f.Point=best;f.Target=b.GoalEnemy?.EnemyProfileId??"";
        return best==null?"no-native-shift-cover-target":"";
    }
}

public sealed partial class Plugin
{
    private static void PatchFlank(Harmony h)
    {
        var type=AccessTools.TypeByName("SAIN.Layers.Combat.Solo.ShiftCoverAction")
            ?? typeof(SAIN.Components.BotComponent).Assembly.GetType("SAIN.Layers.Combat.Solo.ShiftCoverAction");
        // Resolve once at startup against the pinned SAIN binary.
        if(type==null)foreach(var candidate in typeof(BotComponent).Assembly.GetTypes())
            if(candidate.Name=="ShiftCoverAction"){type=candidate;break;}
        if(type==null || AccessTools.Field(type,"NewPoint")?.FieldType!=typeof(CoverPoint))
            throw new MissingFieldException("SAIN ShiftCoverAction.NewPoint");
        var method=AccessTools.Method(type,"FindPointToGo");
        if(method==null||method.ReturnType!=typeof(bool))throw new MissingMethodException("SAIN ShiftCoverAction.FindPointToGo");
        h.Patch(method,prefix:new HarmonyMethod(typeof(Plugin),nameof(SelectFlankPoint)));
    }

    private static bool SelectFlankPoint(object __instance,ref CoverPoint ___NewPoint,ref bool __result)
    {
        var p=Instance;var b=FaultOwner(__instance);
        if(p?.config?.Enabled!=true||b==null||!p.states.TryGetValue(b,out var s)
            ||s.Feature!="A01"||s.ActiveLabel!="COMET:Flank"||!p.TacticalScope(b,b.GoalEnemy)
            ||NativeChoiceHasPriority(b,s)||b.Decision.CurrentCombatDecision!=ECombatDecision.ShiftCover
            ||b.Decision.CurrentSelfDecision!=ESelfActionType.None||b.Decision.CurrentSquadDecision!=ESquadDecision.None)return true;
        var f=s.Flank;var point=f.Point;
        if(point!=null&&f.Target==b.GoalEnemy?.EnemyProfileId&&VisibleAdapter.ValidCover(b,point)
            &&b.Mover.GoToCoverPoint(point,false))
        {
            ___NewPoint=point;__result=true;f.MoveAccepted=true;f.Reason="";
            p.Emit(s,"evaluated","flank-route-accepted");
        }
        else
        {
            f.Refuse("flank-route-refused");f.MoveAccepted=false;__result=false;
            s.Engine.Fail(Tactic.Flank,Time.time,f.Reason);
            p.Emit(s,"rejected",f.Reason);
        }
        return false;
    }
}
