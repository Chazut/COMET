using System;
using Comet.Core;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;

namespace Comet.Client;

public sealed partial class Plugin
{
    // A hidden-contact tactical choice can replace ordinary search/cover work, not
    // grenade avoidance, melee, self-care or an unknown native combat decision.
    private static bool CrossfireTacticalDecision(BotComponent b) => b.Decision.CurrentCombatDecision
        is ECombatDecision.None or ECombatDecision.Search or ECombatDecision.Freeze
        or ECombatDecision.StandAndShoot or ECombatDecision.SeekCover or ECombatDecision.ShiftCover
        or ECombatDecision.MoveToEngage or ECombatDecision.ShootDistantEnemy or ECombatDecision.CreepOnEnemy;
    private static bool CrossfireCanHandoff(BotComponent b) => CrossfireTacticalDecision(b)
        && b.Decision.CurrentCombatDecision is ECombatDecision.Search or ECombatDecision.SeekCover
        or ECombatDecision.ShiftCover or ECombatDecision.MoveToEngage or ECombatDecision.CreepOnEnemy;
    private static void RememberCrossfireHandoff(BotComponent b,CrossfireRuntime r)
    {
        r.HandoffPath=CrossfireCanHandoff(b)?b.Mover.ActivePath:null;
        r.HandoffStarted=r.HandoffPath?.TimeStarted??0;
    }
    private bool PendingCrossfireHandoff(BotComponent b,CrossfireRuntime r)
    {
        // The decision manager commits Freeze before BigBrain starts our action.
        // Sprint can still reflect the outgoing path during this handoff.
        var path=b.Mover.ActivePath;
        return r.Policy.Active&&r.HandoffPath!=null&&CrossfireTacticalDecision(b)
            &&!QueuedCombatMovement(b)&&(path==null||path is BotPathDataManual
                &&ReferenceEquals(path,r.HandoffPath)&&path.TimeStarted==r.HandoffStarted);
    }
    private bool TakeCrossfireHandoff(BotComponent b,BotState s)
    {
        var r=s.Crossfire;var path=b.Mover.ActivePath;
        if(QueuedCombatMovement(b))return false;
        if(path==null){r.HandoffPath=null;return true;}
        if(!CrossfireTacticalDecision(b)||!ReferenceEquals(path,r.HandoffPath)||path.TimeStarted!=r.HandoffStarted
            ||path is not BotPathDataManual manual||!b.Player.MovementContext.IsGrounded)return false;
        // Only after BigBrain selected our action, and only the path observed when
        // planning. Never cancel a new route installed by an emergency or another mod.
        combatStopPath.Invoke(manual,new object[]{false,"COMET squad tactical handoff"});
        r.HandoffPath=null;
        RecordCrossfireGate(s,"squad-tactical-route-handoff",true);
        return b.Mover.ActivePath==null&&!QueuedCombatMovement(b);
    }
    private void RecordCrossfireGate(BotState s,string reason,bool force=false,string feature="S03")
    {
        var r=s.Crossfire;
        if(!force&&r.EligibilityLogAt.TryGetValue(feature,out var next)&&Time.time<next)
        {r.EligibilitySkipped.TryGetValue(feature,out int count);r.EligibilitySkipped[feature]=count+1;return;}
        r.EligibilitySkipped.TryGetValue(feature,out int skipped);r.EligibilitySkipped[feature]=0;
        r.EligibilityLogAt[feature]=Time.time+5;
        r.GateKey=reason;r.GateLogAt=Time.time+5;
        // Eligibility is diagnostic evidence, not a claimed action on the RR map.
        diagnostics?.Write(Diagnostics.Serialize(new{schema=1,session,sequence=++sequence,
            feature,stage="eligibility",reason,skipped,realtime=Time.realtimeSinceStartup,
            profileId=s.ProfileId,name=s.Name,botInstance=s.Id}));
    }
    private bool DeclineSquad(BotState s,string feature,string reason)
    {RecordCrossfireGate(s,reason,feature:feature);return false;}
}
