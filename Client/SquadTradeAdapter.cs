using System;
using System.Collections.Generic;
using Comet.Core;
using SAIN.Components;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

internal sealed class SquadTradeRuntime
{
    internal readonly SquadTradeMemory Memory = new();
    internal int GroupStamp;
    internal float DeathAt,
        Started,
        LastCallback,
        FaceAt = -1;
    internal bool Confirmed,
        Push;
}

public sealed partial class Plugin
{
    private string SquadTradeBlock(BotComponent b, BotState s)
    {
        if (!CrossfireScope(b) || !config!.SquadTrade.Enabled)
            return "trade-out-of-scope";
        if (TradeGroup(b) != s.Crossfire.Trade.GroupStamp)
            return "trade-squad-changed";
        return CrossfireCommonBlock(b, s, true, allowGoalVisible: true);
    }

    private CrossfireConfig TradeMotionConfig() =>
        new()
        {
            Enabled = config!.Enabled && config.SquadTrade.Enabled,
            Participation = 100,
            Spacing = 3,
            MaxPath = config.SquadTrade.MaxPath,
            StallTimeout = config.SquadTrade.StallTimeout,
            MoveTimeout = 12,
            HoldTimeout = 20,
            Retry = config.Crossfire.Retry,
        };

    private bool TryBeginSquadTrade(BotComponent b, BotState s)
    {
        var r = s.Crossfire;
        var a = r.Trade;
        var c = config!.SquadTrade;
        if (!c.Enabled || r.Policy.Active || Time.time < r.Policy.RetryAt)
            return false;
        var loss = a.Memory.Peek(Time.time, c);
        if (loss == null)
            return DeclineSquad(s, "S05", "trade-no-ready-observed-teammate-loss");
        a.GroupStamp = loss.Group;
        string temporary = SquadTradeBlock(b, s);
        if (temporary.Length > 0)
            return DeclineSquad(s, "S05", "trade-memory-retained: " + temporary);
        if (!r.Policy.Begin(Time.time, TradeMotionConfig(), 0))
            return false;
        RememberCrossfireHandoff(b, r);
        r.Maneuver = null;
        r.ManeuverRole = null;
        r.Entrance.Drop();
        a.DeathAt = (float)loss.At;
        a.Started = a.LastCallback = Time.time;
        a.FaceAt = -1;
        a.Confirmed = false;
        r.Origin = b.Position;
        r.Support = loss.Mate;
        r.Reference = b.Position;
        r.Group = b.Squad.Members != null ? ManeuverGroup(b) : "";
        r.Contact = "";
        r.Known = b.Position;
        r.ContactAt = Time.time;
        r.Route = null;
        r.Candidate = r.Sample = 0;
        r.Rejects.Clear();
        r.Samples.Clear();
        r.Candidates.Clear();
        var t = r.Policy.Trace!;
        var d = t.Trade = new SquadTradeTrace
        {
            Death = loss.Id,
            Mate = loss.Mate,
            MateName = loss.Name,
            DeathAge = Time.time - loss.At,
            Delay = loss.ReadyAt - loss.At,
            Ammo = Math.Max(0, Math.Min(100, TacticalAmmoPercent(b.BotOwner))),
        };
        t.Support = loss.Mate;
        t.SupportName = loss.Name;
        string block = SquadTradeBlock(b, s);
        if (block.Length > 0)
        {
            d.Reason = block;
            EndCrossfire(b, s, block);
            return false;
        }
        var chosen = b.GoalEnemy;
        if (
            chosen == null
            || chosen.IsVisible
            || b.BotOwner.Memory.IsUnderFire
            || chosen.KnownPlaces.LastKnownPosition is not Vector3 known
            || chosen.TimeSinceLastKnownUpdated > c.ContactAge
            || Vector3.ProjectOnPlane(known - b.Position, Vector3.up).magnitude > c.MaxDistance
            || chosen.EnemyProfileId != loss.AssociatedContact
                && chosen.EnemyProfileId != loss.AimedAt
        )
        {
            EndCrossfire(b, s, "tradeOtherEnemy-or-unavailable");
            return false;
        }
        a.Memory.Consume(loss);
        bool aggressive = b.Info.PersonalitySettings.Rush.CanRushEnemyReloadHeal;
        d.Choice = aggressive ? "push" : "watch";
        d.Reason = "native personality";
        d.Chance = 100;
        d.Confidence = "squad attacker association";
        a.Push = aggressive;
        r.Known = known;
        r.Contact = chosen.EnemyProfileId;
        r.ContactAt = Time.time - chosen.TimeSinceLastKnownUpdated;
        t.Contact = r.Contact;
        t.Source = NativeAdapter.Capture(b, chosen, true, false).ContactSource;
        t.X = known.x;
        t.Y = known.y;
        t.Z = known.z;
        t.Reason = "trade-" + d.Choice;
        t.Execution = "choosing position";
        t.Remaining = d.Remaining = c.Duration;
        BuildTradeCandidates(b, s);
        RecordCrossfire(s, true);
        return true;
    }

    private void BuildTradeCandidates(BotComponent b, BotState s)
    {
        var r = s.Crossfire;
        if (r.Trade.Push)
        {
            r.Goal = r.Known;
            return;
        }
        r.Indoor = false;
        BuildCrossfireCandidates(b, r);
    }

    private void ProbeSquadTrade(BotComponent b, BotState s)
    {
        var r = s.Crossfire;
        if (r.Trade.Push)
        {
            if (!b.Mover.CanGoToPoint(r.Known, out var path, false))
            {
                EndCrossfire(b, s, "tradeNoPath");
                return;
            }
            r.Route = path;
            r.Goal = r.Known;
            r.Length = (float)Length(path.corners);
            r.Policy.Ready(r.Goal.x, r.Goal.y, r.Goal.z, 0, r.Length);
            return;
        }
        if (r.Candidate >= r.Candidates.Count && r.Route == null)
        {
            r.Goal = b.Position;
            r.Route = new NavMeshPath();
            r.Length = 0;
            r.Policy.Ready(r.Goal.x, r.Goal.y, r.Goal.z, 0, 0);
            r.Policy.Trace!.Reason = "tradeHoldHere";
            return;
        }
        ProbeCrossfire(b, s);
    }

    private bool RefreshSquadTrade(BotComponent b, BotState s)
    {
        var r = s.Crossfire;
        var a = r.Trade;
        var t = r.Policy.Trace!;
        var d = t.Trade!;
        var c = config!.SquadTrade;
        string block = SquadTradeBlock(b, s);
        if (block.Length > 0)
        {
            EndCrossfire(b, s, block);
            return false;
        }
        d.DeathAge = Math.Max(0, Time.time - a.DeathAt);
        d.Remaining = Math.Max(0, c.Duration - (Time.time - a.Started));
        t.Remaining = d.Remaining;
        t.ContactAge = Math.Max(0, Time.time - r.ContactAt);
        if (Time.time < a.Started || a.Push && d.Remaining <= 0)
        {
            EndCrossfire(b, s, "trade-timeout");
            return false;
        }
        Enemy? observed = null;
        foreach (var e in b.EnemyController.KnownEnemies)
            if (e?.EnemyProfileId == r.Contact)
            {
                observed = e;
                break;
            }
        if (observed == null && b.GoalEnemy?.EnemyProfileId == r.Contact)
            observed = b.GoalEnemy;
        if (!a.Push && observed != null && observed.TimeSinceLastKnownUpdated > 25)
        {
            EndCrossfire(b, s, "trade-enemyInfoStale");
            return false;
        }
        if (!a.Push && b.BotOwner.Memory.IsUnderFire)
        {
            EndCrossfire(b, s, "trade-underFire");
            return false;
        }
        if (
            t.State == "planning"
            && r.ProbeFrame != Time.frameCount
            && doorBudget.TryBegin(Time.frameCount, b)
        )
        {
            r.ProbeFrame = Time.frameCount;
            doorClock.Restart();
            try
            {
                ProbeSquadTrade(b, s);
            }
            finally
            {
                doorClock.Stop();
                doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);
            }
        }
        if (!r.Policy.Active)
            return false;
        if (!r.Policy.Check(Time.time, TradeMotionConfig(), "", true))
        {
            EndCrossfire(b, s, t.Reason);
            return false;
        }
        d.Status =
            t.State == "moving" ? "moving"
            : t.State == "holding" ? (a.Confirmed ? "watching" : "awaiting facing")
            : "selected";
        if (t.State == "holding")
        {
            t.Execution = a.Confirmed
                ? "watching the dated known sector"
                : "waiting for observed facing";
            if (t.Reason == "crossfire-angle-reached")
                t.Reason = "trade-position-reached";
        }
        d.Confirmed = a.Confirmed;
        t.Applied = r.Layer && (t.State == "moving" || t.State == "holding" && a.Confirmed);
        t.ValidFor = t.Applied ? Math.Min(1, d.Remaining) : 0;
        RecordCrossfire(s, false);
        return t.State != "planning";
    }

    private void ExecuteSquadTrade(BotComponent b, BotState s)
    {
        var r = s.Crossfire;
        r.Trade.LastCallback = Time.time;
        ExecuteCrossfire(b, s);
        if (r.Policy.Active && r.Trade.Push && r.Policy.Trace!.State == "holding")
        {
            r.Policy.Trace.Outcome = "reached-killer-position";
            EndCrossfire(b, s, "trade-reachedKillerPosition");
        }
    }

    private void ConfirmSquadTrade(BotComponent b, BotState s)
    {
        var r = s.Crossfire;
        if (r.Policy.Trace?.Trade == null || !r.Layer)
            return;
        r.Trade.Confirmed =
            r.Policy.Trace.State == "holding"
            && Vector3.Angle(b.Player.LookDirection, r.Known - b.Position) <= 30;
        if (r.Trade.Confirmed)
            r.Trade.FaceAt = -1;
        r.Trade.LastCallback = Time.time;
        r.Policy.Trace.Trade.Confirmed = r.Trade.Confirmed;
    }

    private void EndSquadTrade(BotState s, string reason)
    {
        var r = s.Crossfire;
        if (r.Policy.Trace?.Trade == null)
            return;
        r.Policy.Trace.Trade.Status = "ended";
        r.Policy.Trace.Trade.Reason = reason;
        r.Policy.Trace.Trade.Confirmed = r.Trade.Confirmed = false;
    }
}
