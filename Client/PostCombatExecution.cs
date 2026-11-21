using System;
using System.Collections.Generic;
using System.Linq;
using Comet.Core;
using EFT;
using EFT.Ballistics;
using EFT.InventoryLogic;
using SAIN.Components;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

public sealed partial class Plugin
{
    private static bool PostTreatmentInUse(BotComponent b) =>
        b.BotOwner.Medecine.Using || b.Player.HandsController is IMedsController;

    private static bool AtPostCombatCover(BotComponent b, Vector3 point) =>
        Vector3.Distance(b.Position, point) <= 1f;

    private static void SetPostCombatProne(BotComponent b, PostCombatRuntime r, bool prone)
    {
        if (r.ProneRequested == prone)
            return;
        b.Mover.Prone.SetProne(prone);
        r.ProneRequested = prone;
    }

    private void RenewPostCoverWatch(PostCombatRuntime r)
    {
        var c = config!.PostCombat;
        r.Policy.RenewCoverWatch(Time.time, UnityEngine.Random.Range((float)c.WatchMin, (float)c.WatchMax));
    }

    private Vector3? PostRegroupTarget(BotComponent b)
    {
        var leader = b.Squad.LeaderComponent;
        if (b.Squad.IAmLeader || leader == null || leader.IsDead)
            return null;
        var position = leader.Position;
        return Vector3.Distance(b.Position, position) > config!.PostCombat.RegroupDistance
            ? position : (Vector3?)null;
    }

    private void InspectPostMedical(BotComponent b, BotState s)
    {
        var med = b.BotOwner.Medecine;
        var r = s.PostCombat;
        r.MedicalUseful =
            med?.FirstAid?.ShallStartUse() == true || med?.SurgicalKit?.ShallStartUse() == true;
        r.MedicalUrgent = false;
    }

    private void ExecutePostCombat(BotComponent b, BotState s)
    {
        var r = s.PostCombat;
        var t = r.Policy.Trace!;
        if (t.State is "cover" or "reload")
        {
            if (PostTreatmentInUse(b))
            {
                if (r.Path != null || r.RouteStage == "cover")
                    PostStopRoute(b, r);
                return;
            }
            var reload = b.BotOwner.WeaponManager.Reload;
            if (r.Policy.WantsReload && !reload.Reloading)
            {
                r.Policy.Complete("reload", "native reload attempted once");
                if (reload.CanReload(true, out var magazine, out var rounds))
                {
                    b.BotOwner.ShootData.EndShoot();
                    reload.Reloading = true;
                    if (magazine != null)
                        reload.ReloadMagazine(magazine);
                    else if (rounds != null && rounds.Count > 0)
                        reload.ReloadAmmo(rounds);
                }
            }
            if (t.State == "reload" && !t.Reloading && reload.Reloading)
                r.Policy.TryRegroup(r.Heard, PostRegroupTarget(b).HasValue);
            t.Reloading = reload.Reloading;
        }
        if (r.Path != null && t.State != r.RouteStage)
            PostStopRoute(b, r);
        if (t.State == "reload")
            return;
        if (t.State == "watch")
        {
            if (PostTreatmentInUse(b))
                return;
            b.Mover.Stop();
            b.Mover.SetTargetPose(.75f);
            SetPostCombatProne(b, r, r.ProneInOpen);
            t.Execution = r.ProneInOpen ? "watching prone in the open" : "stopped to watch at pose 0.75";
            return;
        }
        if (t.State == "check" && r.Threat is Vector3 contact)
        {
            float remaining = Vector3.Distance(b.Position, contact);
            if (remaining <= 3f)
            {
                r.InspectedThreat = true;
                t.InspectionDistance = remaining;
                r.Policy.Complete("check", "destination reached");
                if (r.RouteStage == "check")
                    PostStopRoute(b, r);
                return;
            }
        }
        if (r.RouteStage.Length > 0 && t.State == r.RouteStage)
        {
            bool regroup = r.RouteStage == "regroup";
            if (r.RouteStage == "cover" && AtPostCombatCover(b, r.Goal))
            {
                r.Protected = true;
                r.ProtectedAt = b.Position;
                r.Policy.Complete("cover", "destination reached");
                PostStopRoute(b, r);
                return;
            }
            if (Time.time >= r.NextMoveOrder)
            {
                Vector3? target = regroup ? PostRegroupTarget(b) : r.Goal;
                if (!target.HasValue || !PostWalkToPoint(b, r, target.Value))
                {
                    if (r.RouteStage == "cover")
                        r.CoverRange = PostCombatCoverRange.None;
                    r.Policy.Complete(r.RouteStage, target.HasValue ? "native movement declined" : "regroup no longer needed");
                    PostStopRoute(b, r);
                }
            }
            return;
        }
        if (t.State == "cover")
        {
            if (!SourceOpenGround.IsOpen(b.Position, r.Threat))
            {
                r.Policy.Complete("cover", "already sheltered");
                return;
            }
            var point = SourceOpenGround.FindCover(b, r.Threat, out var why, out var far);
            if (point.HasValue)
            {
                if (!AtPostCombatCover(b, point.Value))
                    RenewPostCoverWatch(r);
                b.Mover.SetTargetPose(far ? 1 : .8f);
                b.Mover.SetTargetMoveSpeed(far ? 1 : .85f);
                if (PostMove(b, r, point.Value, "cover"))
                    r.CoverRange = far ? PostCombatCoverRange.Far : PostCombatCoverRange.Near;
                else
                {
                    r.CoverRange = PostCombatCoverRange.None;
                    r.Policy.Complete("cover", "native movement declined");
                }
            }
            else
            {
                r.CoverRange = PostCombatCoverRange.None;
                r.ProneInOpen = true;
                SetPostCombatProne(b, r, true);
                r.Policy.Complete("cover", why ?? "no cover; prone in the open");
            }
            return;
        }
        if (t.State == "medical")
        {
            var med = b.BotOwner.Medecine;
            if (b.BotOwner.WeaponManager.Reload.Reloading)
                return;
            if (med.Using)
            {
                b.Mover.Stop();
                b.Mover.SetTargetPose(.6f);
                SetPostCombatProne(b, r, r.ProneInOpen);
                return;
            }
            if (Time.time < r.MedicalNext)
                return;
            r.MedicalNext = Time.time + 1;
            bool started =
                med.FirstAid?.ShallStartUse() == true
                    ? b.SelfActions.DoFirstAid()
                    : med.SurgicalKit?.ShallStartUse() == true && b.SelfActions.DoSurgery();
            if (started)
            {
                b.Mover.Stop();
                b.Mover.SetTargetPose(.6f);
                SetPostCombatProne(b, r, r.ProneInOpen);
            }
            t.Execution = started ? "native treatment started" : "native treatment unavailable";
            return;
        }
        if (t.State is "regroup" or "check")
        {
            if (!doorBudget.TryBegin(Time.frameCount, b))
                return;
            doorClock.Restart();
            try
            {
                var target = t.State == "check" ? r.Threat : PostRegroupTarget(b);
                if (!target.HasValue || !PostMove(b, r, target.Value, t.State))
                    r.Policy.Complete(t.State, "destination unavailable; no repeat");
            }
            finally
            {
                doorClock.Stop();
                doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);
            }
            return;
        }
        t.Execution = "waiting for native hands";
    }

    private static bool PostMove(BotComponent b, PostCombatRuntime r, Vector3 target, string stage)
    {
        SetPostCombatProne(b, r, false);
        if (stage is "regroup" or "check")
        {
            b.Mover.SetTargetPose(1);
            b.Mover.SetTargetMoveSpeed(stage == "check" ? .55f : .8f);
        }
        if (!PostWalkToPoint(b, r, target))
            return false;
        r.RouteStage = stage;
        r.Policy.Trace!.Execution = "native movement requested";
        return true;
    }

    private static bool PostWalkToPoint(BotComponent b, PostCombatRuntime r, Vector3 target)
    {
        if (!b.Mover.WalkToPoint(target))
            return false;
        r.Path = b.Mover.ActivePath;
        r.Goal = target;
        r.NextMoveOrder = Time.time + 1;
        return true;
    }

    private static void PostStopRoute(BotComponent b, PostCombatRuntime r)
    {
        // Never cancel a route installed by another layer, or an actual airborne movement.
        if (
            r.Path is BotPathDataManual path
            && ReferenceEquals(b?.Mover.ActivePath, path)
            && b.Player.MovementContext.IsGrounded
        )
            combatStopPath.Invoke(path, new object[] { false, "COMET post-combat route released" });
        if (b != null)
            SetPostCombatProne(b, r, false);
        r.Path = null;
        r.RouteStage = "";
    }
}
