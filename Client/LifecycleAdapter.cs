using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Comet.Core;
using EFT;
using Newtonsoft.Json;
using SAIN.Components;
using UnityEngine;

namespace Comet.Client;

// Explicit boundaries: this coordinator must not recursively enter the generated C08 guard.
public sealed partial class Plugin
{
    private sealed class Life
    {
        internal string Phase = "",
            Profile = "",
            Name = "",
            Id = "",
            Action = "";
        internal int Generation = 1;
        internal bool Cleaned,
            Failed,
            Terminal;
        internal WeakReference<Player>? Player;
    }

    private ConditionalWeakTable<BotComponent, Life> lives = new();
    private readonly List<WeakReference<BotComponent>> suspendedLives = new();
    private readonly List<BotComponent> lifeSweep = new();
    private static readonly Dictionary<Type, Func<object, bool>> downedReaders = new();
    private bool lifeCleaning;
    private double nextLifeProbe;

    // FikaBot inherits FikaPlayer.Downed. Do not infer downing from zero health.
    internal static bool ReadDowned(Player p)
    {
        var type = p.GetType();
        if (!downedReaders.TryGetValue(type, out var read))
        {
            var prop = type.GetProperty(
                "Downed",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            );
            if (prop == null)
            {
                if (type.FullName?.StartsWith("Fika.", StringComparison.Ordinal) == true)
                    throw new MissingMemberException(type.FullName, "Downed");
                read = _ => false;
            }
            else
            {
                if (
                    prop.PropertyType != typeof(bool)
                    || prop.GetIndexParameters().Length != 0
                    || prop.GetMethod == null
                )
                    throw new MissingMemberException(type.FullName, "bool Downed");
                var arg = Expression.Parameter(typeof(object));
                read = Expression
                    .Lambda<Func<object, bool>>(
                        Expression.Property(Expression.Convert(arg, type), prop),
                        arg
                    )
                    .Compile();
            }
            downedReaders.Add(type, read);
        }
        return read(p);
    }

    private static string LifeStatus(BotComponent b)
    {
        if (b == null || b.GameEnding || b.Player == null)
            return "removed";
        var p = b.Player;
        if (ReadDowned(p))
            return "downed"; // Before IsDead: Fika can report dead health while downed.
        if (p.HealthController == null)
            return "unavailable";
        if (!p.HealthController.IsAlive || b.IsDead)
            return "dead";
        // Player/health can outlive the bot owner during teardown. Do not let
        // deferred action callbacks dereference already-disposed native modules.
        if (
            b.BotOwner == null
            || b.BotOwner.WeaponManager == null
            || b.BotOwner.WeaponManager.Selector == null
        )
            return "unavailable";
        if (LifeSupportedBot(b) && Instance?.Ghost(b.ProfileId) == true)
            return "sleeping";
        return "alive";
    }

    private static bool LifeSupportedBot(BotComponent b)
    {
        return b != null && BotEligibility.Supports(b.Player, Instance?.config);
    }

    partial void GuardLifecycle(FaultScope scope, ref bool allowed)
    {
        if (lifeCleaning || scope.Bot is not BotComponent b)
            return;
        if (scope.State?.Retired == true)
        {
            allowed = false;
            return;
        }
        if (scope.Cleanup)
            return;
        allowed = LifeAllows(b, scope.State);
    }

    partial void GuardFaultReset(BotComponent bot, ref bool allowed)
    {
        allowed = !lives.TryGetValue(bot, out var life) || life.Phase == "alive";
    }

    private bool LifeAllows(BotComponent b, BotState? known = null)
    {
        try
        {
            if (!Authority)
                return false;
        }
        catch
        {
            return false;
        }
        try
        {
            bool tracked = known != null && ReferenceEquals(known.Owner, b);
            if (tracked && known!.Retired)
                return false;
            if (!tracked)
            {
                if (lives.TryGetValue(b, out var existing) && existing.Phase != "alive")
                    return false;
                tracked = states.ContainsKey(b);
            }
            // There is no COMET state to retire before the first decision. In particular,
            // an untracked downed PMC must remain eligible after a later revive.
            if (!tracked)
                return !LifeSupportedBot(b) || LifeStatus(b) == "alive";
            string phase = LifeStatus(b);
            if (phase == "alive")
                return true;
            RetireLife(b, phase, "");
            return false;
        }
        catch (Exception ex)
        {
            if (states.ContainsKey(b))
                RetireLife(b, "unavailable", ex.GetType().Name + ": " + ex.Message);
            return false;
        }
    }

    private void RetireLife(BotComponent b, string phase, string error)
    {
        if (ReferenceEquals(b, null))
            return;
        var life = lives.GetValue(b, _ => new Life());
        if (life.Terminal || life.Phase == phase)
            return;
        life.Phase = phase;
        life.Terminal = phase is "dead" or "removed";
        if (states.TryGetValue(b, out var s))
        {
            life.Profile = s.ProfileId;
            life.Name = s.Name;
            life.Id = s.Id;
            life.Action = s.ActiveLabel.Length > 0 ? s.ActiveLabel : s.Decision;
            if (b != null && b.Player != null)
                life.Player = new(b.Player);
            s.Retired = true;
            if (!life.Cleaned)
            {
                life.Cleaned = true; // Set before invoking callbacks; repeated/reentrant notifications cannot clean twice.
                var trace = CleanupLifecycle(b!, s, phase);
                life.Failed = trace.FailedSteps > 0;
                if (life.Failed)
                {
                    life.Terminal = true;
                    trace.State = "disabled";
                }
                trace.Generation = life.Generation;
                trace.PreviousAction = life.Action;
                if (error.Length > 0)
                    trace.Reason = "lifecycle-state-unavailable";
                RecordLifecycle(life, trace);
            }
            else
                RecordLifecycle(
                    life,
                    new LifecycleTrace
                    {
                        State = phase,
                        Reason = "lifecycle-" + phase,
                        Generation = life.Generation,
                    }
                );
        }
        else if (life.Cleaned)
            RecordLifecycle(
                life,
                new LifecycleTrace
                {
                    State = phase,
                    Reason = "lifecycle-" + phase,
                    Generation = life.Generation,
                }
            );
        if (error.Length > 0)
            SafeFaultLog("C09 lifecycle observation: " + error);
        if (
            !life.Terminal
            && !suspendedLives.Exists(w =>
                w.TryGetTarget(out var other) && ReferenceEquals(other, b)
            )
        )
            suspendedLives.Add(new(b!));
    }

    private void SampleLifecycles()
    {
        // Snapshot only owned bots; no GameWorld scan. Remove after all callbacks have returned.
        lifeSweep.Clear();
        foreach (var b in states.Keys)
            lifeSweep.Add(b);
        foreach (var b in lifeSweep)
        {
            LifeAllows(b);
            if (states.TryGetValue(b, out var s) && s.Retired)
            {
                var cleanup = ForgetLifecycleOperations(b, s);
                if (cleanup.FailedSteps > 0 && lives.TryGetValue(b, out var life))
                {
                    life.Terminal = true;
                    life.Failed = true;
                    faultIsolation.DisableBot(b);
                    cleanup.Generation = life.Generation;
                    RecordLifecycle(life, cleanup);
                }
                states.Remove(b);
                pendingFaultReset.Remove(b);
            }
        }
        lifeSweep.Clear();
        if (Time.unscaledTimeAsDouble < nextLifeProbe)
            return;
        nextLifeProbe = Time.unscaledTimeAsDouble + .25;
        for (int i = suspendedLives.Count - 1; i >= 0; i--)
        {
            if (
                !suspendedLives[i].TryGetTarget(out var b)
                || !lives.TryGetValue(b, out var life)
                || life.Terminal
            )
            {
                suspendedLives.RemoveAt(i);
                continue;
            }
            string phase;
            // An unavailable Fika observation is not evidence of death or failed cleanup.
            // Keep the bot suspended and retry the read at the bounded probe interval.
            try
            {
                if (!Authority)
                    continue;
                phase = LifeStatus(b);
            }
            catch
            {
                continue;
            }
            try
            {
                if (phase is "dead" or "removed")
                {
                    RetireLife(b, phase, "");
                    suspendedLives.RemoveAt(i);
                    continue;
                }
                if (
                    phase != "alive"
                    || config?.Enabled != true
                    || !LifeSupportedBot(b)
                    || !b.BotActive
                    || b.GameEnding
                    || Ghost(life.Profile)
                )
                    continue;
                if (
                    b.ProfileId != life.Profile
                    || life.Player != null
                        && (
                            !life.Player.TryGetTarget(out var original)
                            || !ReferenceEquals(original, b.Player)
                        )
                )
                {
                    life.Terminal = true;
                    suspendedLives.RemoveAt(i);
                    continue;
                }
                if (faultIsolation.BotDisabled(b))
                {
                    life.Terminal = true;
                    suspendedLives.RemoveAt(i);
                    continue;
                }
                string oldId = life.Id;
                var fresh = new BotState(b, config, session); // New generation; never revive a pending action or callback.
                states[b] = fresh;
                life.Id = fresh.Id;
                life.Generation++;
                life.Phase = "alive";
                life.Cleaned = false;
                RecordLifecycle(
                    life,
                    new LifecycleTrace
                    {
                        State = "resumed",
                        Reason = "lifecycle-eligible-again",
                        Generation = life.Generation,
                        FreshState = true,
                        PreviousInstance = oldId,
                    }
                );
                suspendedLives.RemoveAt(i);
            }
            catch (Exception ex)
            {
                life.Failed = true;
                life.Terminal = true;
                faultIsolation.DisableBot(b);
                RecordLifecycle(
                    life,
                    new LifecycleTrace
                    {
                        State = "disabled",
                        Reason = "lifecycle-recovery-failed",
                        Generation = life.Generation,
                        FailedSteps = 1,
                        Failures = new[] { BoundLife(ex.GetType().Name + ": " + ex.Message) },
                    }
                );
                SafeFaultLog("C09 recovery failed: " + ex);
                suspendedLives.RemoveAt(i);
            }
        }
    }

    private void ClearLifecycles()
    {
        lives = new();
        suspendedLives.Clear();
        lifeSweep.Clear();
        nextLifeProbe = 0;
    }

    private static string BoundLife(string text) =>
        text.Length > 512 ? text.Substring(0, 512) : text;

    private void RecordLifecycle(Life life, LifecycleTrace trace)
    {
        try
        {
            if (!Comet.Core.ReplayCapture.Detailed && !Comet.Core.ReplayCapture.RaidReview)
                return;
            var json = Diagnostics.Serialize(
                new
                {
                    schema = 1,
                    session,
                    sequence = ++sequence,
                    feature = "C09",
                    stage = "evaluated",
                    decision = "Lifecycle",
                    reason = trace.Reason,
                    realtime = Time.realtimeSinceStartup,
                    utc = DateTime.UtcNow.ToString("O"),
                    configRevision = config?.Revision ?? 0,
                    profileId = life.Profile,
                    name = life.Name,
                    botInstance = life.Id,
                    lifecycle = trace,
                }
            );
            diagnostics?.Write(json);
            Api.Decisions.Publish(json, WarnFaultSink);
        }
        catch (Exception ex)
        {
            WarnFaultSink(ex);
        }
    }
}
