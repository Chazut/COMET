using System;
using Comet.Core;
using EFT;
using EFT.InventoryLogic;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SAIN.Preset.Shared.Enums;
using UnityEngine;

namespace Comet.Client;

internal sealed class WeaponRangeRuntime
{
    internal readonly WeaponRangePolicy Policy = new();
    internal float Next,
        LogAt = -1000;
    internal (string, string, string, string, string, bool) LogKey;
    internal bool Fault;
}

// Only used during a single synchronous range evaluation. Preserve the first known
// entry for an ID, then use the goal if SAIN has not added it to KnownEnemies yet.
internal readonly struct NativeRangeContacts : IWeaponRangeContacts
{
    private readonly BotComponent bot;
    private readonly Enemy? goal;
    internal NativeRangeContacts(BotComponent bot)
    {
        this.bot = bot;
        goal = bot.GoalEnemy;
    }

    private static double Age(Enemy e) => e.IsVisible ? 0 : e.Seen ? e.TimeSinceSeen : 999;

    public WeaponRangeContact? FindGoal(string id)
    {
        var counts = PerformanceCapture.Work;
        if (counts != null) counts.RangeKnownCandidates += bot.EnemyController.KnownEnemies.Count;
        var match = goal;
        foreach (var e in bot.EnemyController.KnownEnemies)
            if (e != null && e.EnemyProfileId == id)
            {
                match = e;
                break;
            }
        if (match == null || match.EnemyProfileId != id)
            return null;
        if (counts != null) counts.RangeGoalDetailReads++;
        return new WeaponRangeContact { Id = id, Age = Age(match), Distance = match.RealDistance };
    }

    public bool HasRecentDistant(WeaponRangeConfig config)
    {
        var counts = PerformanceCapture.Work;
        if (counts != null) counts.RangeOtherContactScans++;
        bool goalInList = false;
        foreach (var e in bot.EnemyController.KnownEnemies)
        {
            if (e == null) continue;
            if (counts != null) counts.RangeOtherContactsExamined++;
            if (Age(e) <= config.LostContactSeconds && e.RealDistance >= config.CloseDistance)
                return true;
            if (goal != null && e.EnemyProfileId == goal.EnemyProfileId)
                goalInList = true;
        }
        return !goalInList && goal != null && Age(goal) <= config.LostContactSeconds
            && goal.RealDistance >= config.CloseDistance;
    }
}

public sealed partial class Plugin
{
    private bool WeaponRangeScope(BotComponent b) =>
        config?.Enabled == true
        && config.WeaponRange.Enabled
        && NativeAdapter.IsSupportedAi(b)
        && b.BotActive
        && !b.IsDead
        && !b.GameEnding
        && Authority
        && !Ghost(b.ProfileId)
        && b.SAINLayersActive;

    private WeaponReturnConfig RangeArbiterConfig() => config!.WeaponReturn;

    private static WeaponRangeGun ReadRangeGun(Weapon w, WeaponReturnGun ammo)
    {
        var g = new WeaponRangeGun
        {
            Id = ammo.Id,
            Slot = ammo.Slot,
            Name = w.ShortName.Localized(),
            Class = w.WeapClass,
            Loaded = ammo.Loaded,
            Reserve = ammo.Reserve,
            Usable = true,
        };
        WeaponRangeModel.Classify(g);
        return g;
    }

    private WeaponRangeFrame WeaponRangeFrameFor(BotComponent b, BotState s,
        Player.FirearmController firearm, out WeaponReturnGun main, out WeaponReturnGun other)
    {
        var wm = b.BotOwner.WeaponManager;
        using var resources = s.ReloadTiming.ReturnInventory.Begin();
        var first = ReadReturnGun(b, resources, EquipmentSlot.FirstPrimaryWeapon);
        var second = ReadReturnGun(b, resources, EquipmentSlot.SecondPrimaryWeapon);
        bool secondIsMain = wm.Selector._mainWeapon == EquipmentSlot.SecondPrimaryWeapon;
        main = secondIsMain ? second : first;
        other = secondIsMain ? first : second;
        // RR retains this exact ordered pair in its trace. Never reuse a mutable array.
        WeaponRangeGun? Describe(WeaponReturnGun ammo) =>
            Enum.TryParse(ammo.Slot, out EquipmentSlot slot) && SlotGun(b, slot) is Weapon gun
                ? ReadRangeGun(gun, ammo) : null;
        var mainGun = Describe(main);
        var otherGun = Describe(other);
        WeaponRangeGun[] guns;
        if (mainGun != null && otherGun != null)
            guns = new[] { mainGun, otherGun };
        else if ((mainGun ?? otherGun) is WeaponRangeGun only)
            guns = new[] { only };
        else
            guns = Array.Empty<WeaponRangeGun>();
        string heldId = firearm.Item.Id.ToString();
        bool primaryInHands = heldId == first.Id || heldId == second.Id;
        var counts = PerformanceCapture.Work;
        if (counts != null)
        {
            counts.RangeEvaluations++;
            if (!primaryInHands) counts.RangeHolsterIdReads++;
        }
        return new WeaponRangeFrame
        {
            Now = Time.time,
            Current = primaryInHands ? heldId : SlotGun(b, EquipmentSlot.Holster)?.Id.ToString() ?? "",
            Main = main.Id,
            Ready = WeaponHandsReady(b, firearm),
            Reloading = wm.Reload.Reloading,
            Goal = b.GoalEnemy?.EnemyProfileId ?? "",
            Weapons = guns,
        };
    }

    private bool KeepRangeChoice(
        BotComponent b,
        BotState s,
        WeaponReturnFrame f,
        WeaponReturnGun target,
        string origin
    ) =>
        origin == "native-return"
        && config!.WeaponRange.Enabled
        && s.WeaponRange.Policy.Holds(f.Current.Id)
        && f.Current.Loaded + f.Current.Reserve > 0;

    private void SampleWeaponRange(BotComponent b, BotState s)
    {
        var r = s.WeaponRange;
        if (r.Fault)
            return;
        if (!WeaponRangeScope(b))
        {
            EndWeaponRange(s, "range-out-of-scope");
            return;
        }
        var firearm = b.Player.HandsController as Player.FirearmController;
        if (
            firearm == null || !WeaponHandsReady(b, firearm)
            || b.BotOwner.WeaponManager.Reload.Reloading
        )
            return;
        var frame = WeaponRangeFrameFor(b, s, firearm, out var main, out var other);
        string next = r.Policy.Evaluate(frame, config!.WeaponRange, new NativeRangeContacts(b));
        if (next.Length > 0)
        {
            NativeWeaponRequest(b, s, main.Id == next ? main : other, "W05");
            r.Policy.Arbitration(s.WeaponReturn.Policy.Trace, Time.time);
        }
        RecordWeaponRange(b, s);
    }

    private void EndWeaponRange(BotState s, string why)
    {
        if (s.WeaponRange.Policy.Trace == null || s.WeaponRange.Policy.Trace.State == "ended")
            return;
        s.WeaponRange.Policy.Clear(why);
        RecordWeaponRange(null, s, true);
    }

    private void WeaponRangeError(BotState s, Exception ex)
    {
        if (s.WeaponRange.Fault)
            return;
        s.WeaponRange.Fault = true;
        EndWeaponRange(s, "range-adapter-error");
        Logger.LogWarning("W05 yielded to SAIN: " + ex.GetBaseException().Message);
    }

    private void RecordWeaponRange(BotComponent? b, BotState s, bool force = false)
    {
        var r = s.WeaponRange;
        var t = r.Policy.Trace;
        if (t == null)
            return;
        var key = (t.State, t.Reason, t.From, t.To, t.Target, t.Ready);
        if (!force && key == r.LogKey && Time.time - r.LogAt < .25f)
            return;
        r.LogKey = key;
        r.LogAt = Time.time;
        if (b != null)
            t.Action =
                b.Decision.CurrentSelfDecision == ESelfActionType.Reload
                    ? "Reload"
                    : b.Decision.CurrentCombatDecision.ToString();
        var json = Diagnostics.Serialize(
            new
            {
                schema = 1,
                session,
                sequence = ++sequence,
                feature = "W05",
                stage = "evaluated",
                decision = "WeaponRange",
                reason = t.Reason,
                realtime = Time.realtimeSinceStartup,
                utc = DateTime.UtcNow.ToString("O"),
                configRevision = config?.Revision ?? 0,
                profileId = s.ProfileId,
                name = s.Name,
                botInstance = s.Id,
                weaponRange = t,
                weaponRangeConfig = config?.WeaponRange,
            }
        );
        diagnostics?.Write(json);
        Api.Decisions.Publish(json, ex =>
        {
            if (!subscriberWarned)
            {
                subscriberWarned = true;
                Logger.LogWarning(ex.Message);
            }
        });
    }
}
