using System;
using System.Reflection;
using SAIN.Components;
using SAIN.Models.Enums;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes;
using SAIN.SAINComponent.SubComponents.CoverFinder;
using UnityEngine;

namespace Comet.Client;

public sealed partial class Plugin
{
    // zzap fbb86b83 BotDecisionManager.ContinueMoveToCover/ShouldBreakOffRun.
    // Called only after stock SAIN's common continuation checks have succeeded.
    private bool SourceContinueCover(BotComponent bot, BotState state)
    {
        var point=bot.Cover.CoverPoint_MovingTo;
        var enemy=bot.GoalEnemy;
        var weapon=bot.BotOwner.WeaponManager;
        if(bot.Decision.CurrentCombatDecision==ECombatDecision.SeekCover
            && bot.Decision.CurrentSelfDecision==ESelfActionType.None
            && enemy!=null && enemy.IsVisible && enemy.CanShoot && enemy.RealDistance<=30f
            && weapon?._currentWeaponInfo!=null && weapon.Reload?.Reloading!=true && weapon.HaveBullets
            && (point==null || point.PathDistanceStatus is not (CoverStatus.CloseToCover or CoverStatus.InCover)))
        {
            if(Time.time-state.CoverRunBreakCountedAt>3f)
            {
                state.CoverRunBreakCountedAt=Time.time;
                ArbitrationEvent(state,"source-run-to-cover-break-off","SeekCover");
            }
            return false;
        }
        if(bot.Decision.TimeSinceChangeDecision<.5f)
        {state.CoverRunAnchorAt=-1f;return true;}
        var position=bot.Position;
        if(state.CoverRunAnchorAt<0f || (position-state.CoverRunAnchor).sqrMagnitude>.25f)
        {state.CoverRunAnchor=position;state.CoverRunAnchorAt=Time.time;}
        else if(Time.time-state.CoverRunAnchorAt>3f)
        {
            state.CoverRunAnchorAt=-1f;
            if(point!=null)
            {
                if(point.GetDistance(position)<3f) SourceCoverAccess.Accept(bot.Cover);
                else point.CoverData.IsBad=true;
            }
            ArbitrationEvent(state,"source-run-to-cover-stalled",point?.CoverData.IsBad==true?"unreachable":"arrived");
            return false;
        }
        return true;
    }
}

// Original SAIN lacks zzap's AcceptCoverMovingTo. Bind the same four native
// assignments once; this mirrors SAINCoverClass's own arrival transition.
internal static class SourceCoverAccess
{
    private const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    private static T Bind<T>(string name) where T:Delegate => (T)Delegate.CreateDelegate(typeof(T),typeof(SAINCoverClass).GetMethod(name,Flags)!);
    private static readonly Action<SAINCoverClass,CoverPoint> SetInUse=Bind<Action<SAINCoverClass,CoverPoint>>("set_CoverInUse");
    private static readonly Action<SAINCoverClass,CoverPoint> SetMovingTo=Bind<Action<SAINCoverClass,CoverPoint>>("set_CoverPoint_MovingTo");
    private static readonly FieldInfo ShallSprint=typeof(SAINCoverClass).GetField("_shallSprint",Flags)!;
    private static readonly object False=false;
    private static readonly Action<SAINCoverClass,ECoverSeekingState> SetState=Bind<Action<SAINCoverClass,ECoverSeekingState>>("SetCoverSeekingState");

    internal static void Accept(SAINCoverClass cover)
    {
        var point=cover.CoverPoint_MovingTo;
        if(point==null)return;
        SetInUse(cover,point);
        SetMovingTo(cover,null!);
        ShallSprint.SetValue(cover,False);
        SetState(cover,ECoverSeekingState.HoldInCover);
    }

    internal static void Forget(SAINCoverClass cover, CoverPoint point)
    {
        // Release only this invalid seasonal point; preserve another valid route.
        if (ReferenceEquals(cover.CoverInUse, point)) SetInUse(cover, null!);
        if (ReferenceEquals(cover.CoverPoint_MovingTo, point)) SetMovingTo(cover, null!);
    }
}
