using System;
using System.Collections.Generic;
using System.Linq;
using Comet.Core;
using EFT;
using SAIN.Components;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SAIN.SAINComponent.SubComponents.CoverFinder;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

internal sealed class KnownThreat
{
    internal string Id = "";
    internal Vector3 Position;
    internal double Age;
    internal bool Visible;
}
internal sealed class ExposureProbe
{
    internal float At;
    internal Vector3 Origin;
    internal CoverPoint? Cover;
    internal List<KnownThreat> Threats = new List<KnownThreat>();
    internal double Current, CoverEnd, CoverRoute, PushRoute;
    internal bool PushComplete, UnderFire;
    internal double HitAt;
    internal float PushLength;
    internal int Rays;
    internal double Milliseconds;
    internal int CoverPathSignature, PushPathSignature;
    internal double CoverLength, CoverVertical, CoverStartError;
    internal int CoverCorners;
    internal double[] CoverSamples = Array.Empty<double>();
}

internal static class VisibleAdapter
{
    internal static bool WeaponReady(BotComponent bot) => bot.BotOwner.WeaponManager?.HaveBullets == true;
    internal static bool ValidCover(BotComponent bot, CoverPoint? p) => p != null && !p.CoverData.IsBad && !p.Spotted;
    internal static bool ValidPath(NavMeshPath? path, Vector3 start, Vector3 end)
        => path?.status == NavMeshPathStatus.PathComplete;
    internal static CoverPoint? SelectCover(BotComponent bot, PlanProgress? progress = null)
    {
        CoverPoint? best=null;float distance=99;
        var enemy=bot.GoalEnemy;
        if(enemy==null)return bot.Cover.CoverInUse ?? bot.Cover.CoverPoint_MovingTo;
        // VisibleEnemyUtility.NearestCover: one line to THIS enemy, nearest distance.
        Vector3 eye=(enemy.IsVisible?enemy.EnemyPosition:enemy.KnownPlaces.LastKnownPosition??bot.Position)+Vector3.up*1.5f;
        foreach(var p in bot.Cover.CoverPoints)
        {
            if(p==null)continue;
            float d=Vector3.Distance(p.Position,bot.Position);
            if(d>=distance||!Physics.Linecast(eye,p.Position+Vector3.up,LayersMaskController.HighPolyWithTerrainMask))continue;
            best=p;distance=d;
        }
        return best;
    }
    internal static VisibleSituation Capture(BotComponent bot, Enemy enemy, Situation contact, BotState state, CometConfig config)
    {
        var cover=SelectCover(bot);
        var v=new VisibleSituation { Contact=contact,WeaponReady=WeaponReady(bot),
            CanShoot=enemy.CanShoot&&enemy.RealDistance<=bot.Info.WeaponInfo.EffectiveWeaponDistance*1.25,
            RecentHit=bot.Medical.TimeSinceShot<1.5f,RecentHitAt=Time.time-bot.Medical.TimeSinceShot,
            ProbeReady=true,CoverReachable=cover!=null,
            PushReachable=enemy.Path.PathToEnemy?.status==NavMeshPathStatus.PathComplete,
            PushPathLength=Math.Max(0,enemy.Path.PathLength),ThreatCount=1,ThreatSignature=enemy.EnemyProfileId,
            CoverId=cover?.HardData.Id.ToString()??"",CoverDestination=ProgressAdapter.CoverKey(cover),
            CurrentExposure=Math.Min(1,Math.Max(0,(Time.time-enemy.Vision.VisibleStartTime)/Math.Max(.5f,bot.Info.HoldGroundDelay))),
            CoverDistance=cover==null?99:Vector3.Distance(bot.Position,cover.Position),CoverExposure=cover==null?1:0,
            ContinuationSafe=true,ContinuationReason="zzap-ranked-plan" };
        state.SelectedCover=cover;
        state.Probe=new ExposureProbe {At=Time.time,Origin=bot.Position,Cover=cover,Current=v.CurrentExposure,
            CoverEnd=v.CoverExposure,PushComplete=v.PushReachable,PushLength=(float)v.PushPathLength,
            UnderFire=bot.BotOwner.Memory.IsUnderFire,HitAt=v.RecentHitAt};
        return v;
    }
}
