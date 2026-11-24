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
    internal string Item = "",
        LogKey = "";
    internal int Enemy;
    internal long Revision;
    internal bool ShortFusePreferred;
    internal bool UseNativeFuse;
    internal double ChosenAt,
        Power,
        LogAt = -1000;
    internal Vector3 Known;
    // No item, controller, enemy or GameWorld references survive raid teardown.
}

public sealed partial class Plugin
{
    private bool GrenadeChoiceScope(BotComponent b) =>
        config?.Enabled == true
        && config.GrenadeChoice.Enabled
        && NativeAdapter.IsSupportedAi(b)
        && b.BotActive
        && !b.GameEnding
        && Authority
        && !Ghost(b.ProfileId);

    private bool GrenadeChoiceInProgress(BotComponent b) =>
        GrenadeChoiceScope(b) && b.BotOwner.WeaponManager.Grenades.ThrowindNow;

    private bool ChooseGrenade(BotComponent b, Enemy e, BotState s, bool arc)
    {
        if (!GrenadeChoiceScope(b))
            return arc;
        var r = s.GrenadeChoice;
        var g = b.BotOwner.WeaponManager.Grenades;
        if (!arc)
        {
            StopGrenadeChoice(s, "choice-no-trajectory", "blocked");
            return false;
        }
        if (g.ThrowindNow)
        {
            StopGrenadeChoice(s, "choice-native-throw-in-progress", "blocked");
            return false;
        }
        var f = GrenadeFrame(b, e, s);
        string evidence = ChoiceEvidence(b, e, f);
        if (evidence.Length > 0)
        {
            StopGrenadeChoice(s, evidence, "blocked");
            return false;
        }
        var d = g.AIGreanageThrowData;
        if (d == null || !ValidGrenadeArc(d) || !d.IsUpToDate() || d.ThrowComplete)
        {
            StopGrenadeChoice(s, "choice-arc-expired", "blocked");
            return false;
        }
        bool underRoof = GrenadeThrowerUnderRoof(b);
        bool shortUnderRoof = config!.GrenadeChoice.PreferShortIndoors && underRoof;
        double distance = Vector3.Distance(d.Position, d.Target);
        bool shortest = shortUnderRoof || distance < config.GrenadeChoice.ShortFuseDistance;
        if (
            r.Item.Length > 0
            && !r.UseNativeFuse
            && r.Item == f.Item
            && r.ShortFusePreferred == shortest
            && r.Enemy == f.Instance
            && r.Revision == config.Revision
            && f.Now - r.ChosenAt <= 2
            && Vector3.Distance(r.Known, new((float)f.X, (float)f.Y, (float)f.Z)) <= 1
        )
            return ValidateGrenadeChoice(b, e, s, false); // Same opportunity keeps the same item, even with a new arc.

        var flat = d.Target - d.Position;
        flat.y = 0;
        var flight = GrenadeChoiceRules.Flight(flat.magnitude, d.Force, d.Ang);
        r.Trace = new()
        {
            Target = f.Target,
            Source = f.Source,
            KnownX = f.X,
            KnownY = f.Y,
            KnownZ = f.Z,
            ThrowerUnderRoof = underRoof,
            Distance = distance,
            Flight = flight ?? 0,
            RequiredMargin = 0,
            Preference = shortUnderRoof ? "short-indoors" : shortest ? "short-close" : "long-outdoors",
        };
        r.Item = "";
        r.UseNativeFuse = false;
        flight ??= 0;
        var items = new Dictionary<string, ThrowWeap>();
        foreach (var carried in b.Player.Inventory.GetPlayerItems(EPlayerItems.Equipment))
        {
            if (carried is not ThrowWeap item)
                continue;

            string id = item.Id.ToString();
            if (id.Length == 0 || items.ContainsKey(id))
            {
                StopGrenadeChoice(s, "choice-ambiguous-item", "blocked");
                return false;
            }
            items.Add(id, item);
            string why = ChoiceReject(b, item, flight.Value);
            r.Trace.Alternatives.Add(
                new()
                {
                    Item = id,
                    Template = item.TemplateId.ToString(),
                    Name = item.ShortName.Localized(),
                    Fuse = GrenadeChoiceRules.Finite(item.GetExplDelay) ? item.GetExplDelay : null,
                    Category = GrenadeCategory(item),
                    Eligible = why.Length == 0,
                    Reason = why.Length == 0 ? "eligible" : why,
                }
            );
        }
        var fragments = r.Trace.Alternatives.FindAll(a =>
            a.Category == "fragmentation"
            || !GrenadeFilterScope(b) && items[a.Item].ThrowType.ToString() == "frag_grenade"
        );
        var pick = GrenadeChoiceRules.Choose(fragments, shortest);
        if (pick == null && GrenadeFilterScope(b) && config.GrenadeFilter.AllowFlash)
        {
            foreach (var a in r.Trace.Alternatives)
                if (
                    a.Category == "flash"
                    && a.Eligible
                    && (
                        pick == null
                        || a.Item == f.Item
                        || pick.Item != f.Item && string.CompareOrdinal(a.Item, pick.Item) < 0
                    )
                )
                    pick = a;
            if (pick != null)
                r.Trace.Preference = "flash-fallback";
        }
        float oldPower = g.MaxPower;
        r.ShortFusePreferred = shortest;
        r.Enemy = f.Instance;
        r.Revision = config.Revision;
        r.Known = new((float)f.X, (float)f.Y, (float)f.Z);
        r.ChosenAt = f.Now;
        if (pick == null)
        {
            r.UseNativeFuse = true;
            r.Item = f.Item;
            r.Power = oldPower;
            if (!ValidateGrenadeChoice(b, e, s, false))
                return false;
            r.Trace.State = "ended";
            r.Trace.Reason = "choice-native-fuse-retained";
            r.Trace.ValidFor = 0;
            RecordGrenadeChoice(s);
            return true;
        }
        g.SetThrowParams(items[pick.Item]); // Native inventory/animation/Fika execution stays intact.
        r.Item = pick.Item;
        r.Power = g.MaxPower;
        r.Trace.Item = pick.Item;
        r.Trace.Template = pick.Template;
        r.Trace.Name = pick.Name;
        r.Trace.Fuse = pick.Fuse!.Value;
        r.Trace.Margin = pick.Fuse.Value - flight.Value;
        foreach (var a in r.Trace.Alternatives)
            if (a.Eligible)
                a.Reason =
                    a.Item == pick.Item ? "selected"
                    : a.Category == "flash" ? "flash-reserve"
                    : a.Fuse == pick.Fuse ? "same-fuse-later-in-inventory"
                    : "less-preferred-fuse";
        // Fixed native mass currently makes power identical. Fail closed if a future mod changes that contract.
        if (!GrenadeChoiceRules.Finite(g.MaxPower) || Math.Abs(g.MaxPower - oldPower) > .001)
        {
            FailGrenadeTrajectory(s, "trajectory-item-power-changed");
            StopGrenadeChoice(s, "choice-item-needs-new-arc", "blocked");
            return false;
        }
        s.GrenadeJudgment.ArcItem = r.Item;
        if (GrenadeTrajectoryScope(b))
            s.GrenadeTrajectory.Item = r.Item;
        // Native DoThrow otherwise selects the first grenade of this type, silently discarding our choice.
        d.GrenadeType = null;
        if (!ValidateGrenadeChoice(b, e, s, false))
            return false;
        r.Trace.State = "selected";
        r.Trace.Reason = "choice-" + r.Trace.Preference;
        r.Trace.ValidFor = .75;
        RecordGrenadeChoice(s);
        return true;
    }

    private string ChoiceEvidence(BotComponent b, Enemy e, GrenadeJudgmentFrame f)
    {
        if (
            !ReferenceEquals(e, b.GoalEnemy)
            || !new DoorPoint(f.X, f.Y, f.Z).Finite
            || !GrenadeChoiceRules.Finite(f.EvidenceAt)
            || f.Now < f.EvidenceAt
            || f.Now - f.EvidenceAt > config!.GrenadeContactLifetime
        )
            return "choice-no-fresh-personal-contact";
        return f.Block;
    }

    private string ChoiceReject(BotComponent b, ThrowWeap item, double flight)
    {
        string filtered = FilterReject(b, item);
        if (filtered.Length > 0)
            return filtered;
        if (GrenadeFilterScope(b) && GrenadeCategory(item) == "flash")
            return GrenadeFilterRules.FlashReject(
                item.GetExplDelay,
                item.MinTimeToContactExplode,
                flight,
                0
            );
        return GrenadeChoiceRules.Reject(
            item.GetExplDelay,
            item.MinTimeToContactExplode,
            item.IsDummy,
            item.ThrowType.ToString() == "frag_grenade",
            flight,
            config!.GrenadeChoice
        );
    }

    private bool ValidateGrenadeChoice(BotComponent b, Enemy e, BotState s, bool execution)
    {
        if (!GrenadeChoiceScope(b))
            return true;
        var r = s.GrenadeChoice;
        var g = b.BotOwner.WeaponManager.Grenades;
        var f = GrenadeFrame(b, e, s);
        var d = g.AIGreanageThrowData;
        string why = ChoiceEvidence(b, e, f);
        if (
            why.Length == 0
            && (
                g.ThrowindNow
                || r.Item.Length == 0
                || r.Item != f.Item
                || r.Enemy != f.Instance
                || r.Revision != config!.Revision
            )
        )
            why = "choice-binding-changed";
        if (
            why.Length == 0
            && (d == null || !ValidGrenadeArc(d) || !d.IsUpToDate() || d.ThrowComplete)
        )
            why = "choice-arc-expired";
        if (
            why.Length == 0
            && (!GrenadeChoiceRules.Finite(g.MaxPower) || Math.Abs(g.MaxPower - r.Power) > .001)
        )
            why = "choice-throw-power-changed";
        double flight = 0;
        if (why.Length == 0 && !r.UseNativeFuse)
        {
            var flat = d!.Target - d.Position;
            flat.y = 0;
            flight = GrenadeChoiceRules.Flight(flat.magnitude, d.Force, d.Ang) ?? 0;
        }
        if (why.Length == 0)
        {
            bool carried = false;
            foreach (var item in b.Player.Inventory.GetPlayerItems(EPlayerItems.Equipment))
            {
                if (ReferenceEquals(item, g.grenade))
                {
                    carried = true;
                    break;
                }
            }
            if (!carried)
                why = "choice-item-no-longer-carried";
            else if (!r.UseNativeFuse)
                why = ChoiceReject(b, g.grenade!, flight);
        }
        if (why.Length == 0)
            why = GrenadeSafety(b, s, f);
        if (why.Length == 0 && !NativeAdapter.GrenadeFriendsClear(b, d!.Target))
            why = "sain-friendly-distance";
        if (why.Length > 0)
        {
            StopGrenadeChoice(s, why, "blocked");
            return false;
        }
        if (r.UseNativeFuse)
            return true;
        d!.GrenadeType = null;
        r.Trace!.Flight = flight;
        r.Trace.Fuse = g.grenade!.GetExplDelay;
        r.Trace.Margin = r.Trace.Fuse - flight;
        if (execution)
        {
            r.Trace.State = "validated";
            r.Trace.Reason = "choice-execution-validated";
            r.Trace.ValidFor = .75;
            RecordGrenadeChoice(s, true);
        }
        return true;
    }

    private void StopGrenadeChoice(BotState s, string reason, string state = "ended")
    {
        var r = s.GrenadeChoice;
        r.Item = "";
        r.UseNativeFuse = false;
        if (r.Trace == null)
            return;
        r.Trace.State = state;
        r.Trace.Reason = reason;
        r.Trace.ValidFor = state == "ended" ? 0 : .75;
        RecordGrenadeChoice(s);
    }

    private void RecordGrenadeChoice(BotState s, bool force = false)
    {
        var r = s.GrenadeChoice;
        var t = r.Trace;
        if (t == null)
            return;
        string key = t.State + "|" + t.Reason + "|" + t.Item;
        if (!force && key == r.LogKey && Time.timeAsDouble - r.LogAt < .75)
            return;
        r.LogKey = key;
        r.LogAt = Time.timeAsDouble;
        if (!Comet.Core.ReplayCapture.Detailed)
            return;
        var json = Diagnostics.Serialize(
            new
            {
                schema = 1,
                session,
                sequence = ++sequence,
                feature = "G03",
                stage = "evaluated",
                decision = "GrenadeChoice",
                reason = t.Reason,
                realtime = Time.realtimeSinceStartup,
                utc = DateTime.UtcNow.ToString("O"),
                configRevision = config?.Revision ?? 0,
                profileId = s.ProfileId,
                name = s.Name,
                botInstance = s.Id,
                grenadeChoice = t,
                grenadeChoiceConfig = config?.GrenadeChoice,
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
