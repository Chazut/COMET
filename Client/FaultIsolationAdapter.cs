using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using Comet.Core;
using Newtonsoft.Json;
using SAIN.Components;
using UnityEngine;

namespace Comet.Client;

// The build-time boundary manifest is in Client/obj/fault-boundaries/boundaries.json.
// The normal path uses a reusable value-type stack. Only errors allocate quarantine state.
public sealed partial class Plugin
{
    internal struct FaultScope
    {
        internal int Index;
        internal string Module,
            Entry;
        internal BotComponent? Bot;
        internal BotState? State;
        internal bool Cleanup,
            Telemetry;
    }

    private sealed class FaultOrigin
    {
        internal FaultScope Scope;

        internal FaultOrigin(FaultScope scope)
        {
            Scope = scope;
        }
    }

    private sealed class FaultBlocked : Exception
    {
        internal FaultBlocked()
            : base("COMET dependency unavailable") { }
    }

    private readonly FaultIsolation faultIsolation = new();
    private readonly HashSet<BotComponent> pendingFaultReset = new();
    private readonly object globalFaultKey = new();

    [ThreadStatic]
    private static List<FaultScope>? faultStack;

    [ThreadStatic]
    private static bool faultCleaning,
        faultCleanupFailed;

    [ThreadStatic]
    private static Exception? faultCleanupError;
    private static readonly Dictionary<Type, Func<object, object?>> faultOwners = new();
    private bool faultSinkWarned;
    private bool faultInsideCoverScan;

    partial void ConfigureCoverRetry(BotComponent b, ref Func<bool>? retry)
    {
        retry = () => CoverScanMode(b) == 1;
    }

    partial void CoverScanFailure(BotComponent b, Exception ex, ref bool handled)
    {
        handled = true;
        faultInsideCoverScan = true;
        try
        {
            QuarantineFault(
                new FaultScope
                {
                    Bot = b,
                    Module = "CoverScan",
                    Entry = "MoveNext",
                },
                ex
            );
        }
        finally
        {
            faultInsideCoverScan = false;
        }
    }

    private static readonly FaultIsolationConfig defaultFaultConfig = new();
    private static readonly object faultOriginKey = new();

    internal static FaultScope EnterFault(
        string module,
        string entry,
        BotComponent? bot,
        bool cleanup,
        bool telemetry
    )
    {
        var stack = faultStack ??= new(32);
        var scope = new FaultScope
        {
            Index = stack.Count,
            Module = module,
            Entry = entry,
            Bot = bot ?? (stack.Count > 0 ? stack[stack.Count - 1].Bot : null),
            Cleanup = cleanup,
            Telemetry = telemetry,
        };
        if (stack.Count > 0 && ReferenceEquals(scope.Bot, stack[stack.Count - 1].Bot))
            scope.State = stack[stack.Count - 1].State;
        stack.Add(scope);
        return scope;
    }

    internal static void BindFault(ref FaultScope scope, BotComponent? bot)
    {
        if (bot != null)
        {
            if (!ReferenceEquals(scope.Bot, bot))
                scope.State = null;
            scope.Bot = bot;
            faultStack![scope.Index] = scope;
        }
    }

    internal static BotComponent? FaultBot(BotComponent? bot, BotState? state) =>
        bot ?? state?.Owner;

    internal static void BindFaultState(ref FaultScope scope, BotState? state)
    {
        scope.State = state;
        faultStack![scope.Index] = scope;
    }

    partial void GuardLifecycle(FaultScope scope, ref bool allowed);

    partial void GuardFaultReset(BotComponent bot, ref bool allowed);

    internal static void LeaveFault(FaultScope scope)
    {
        if (faultStack != null && scope.Index < faultStack.Count)
            faultStack.RemoveRange(scope.Index, faultStack.Count - scope.Index);
    }

    internal static bool LocalFaultCatch() => faultStack == null || faultStack.Count == 0;

    private static bool NestedFault(FaultScope s) =>
        !s.Cleanup
        && !s.Telemetry
        && s.Index > 0
        && s.Bot != null
        && ReferenceEquals(faultStack![s.Index - 1].Bot, s.Bot)
        && !faultStack[s.Index - 1].Cleanup
        && !faultStack[s.Index - 1].Telemetry;

    partial void GuardRaid(FaultScope scope, ref bool allowed);

    internal static bool AllowFault(FaultScope s)
    {
        var p = Instance;
        if (p == null || s.Telemetry || faultCleaning)
            return true;
        bool live = true;
        p.GuardRaid(s, ref live);
        if (live)
            p.GuardLifecycle(s, ref live);
        if (!live)
        {
            if (NestedFault(s))
                throw new FaultBlocked();
            return false;
        }
        if (s.Cleanup)
            return true;
        if (!p.faultIsolation.HasFaults)
            return true;
        object key = (object?)s.Bot ?? p.globalFaultKey;
        FaultTrace? retry = null;
        bool allowed =
            (s.Bot == null || !p.pendingFaultReset.Contains(s.Bot))
            && p.faultIsolation.Ready(key, s.Module, Time.unscaledTimeAsDouble, out retry);
        // Global infrastructure failures also quarantine that service for its consumers.
        if (s.Bot != null)
        {
            if (
                !p.faultIsolation.Ready(
                    p.globalFaultKey,
                    s.Module,
                    Time.unscaledTimeAsDouble,
                    out var serviceRetry
                )
            )
                allowed = false;
            else if (serviceRetry != null)
                p.RecordFault(null, serviceRetry);
        }

        // One transition event, not one event per skipped frame.
        if (allowed && retry != null)
            p.RecordFault(s.Bot, retry);
        if (!allowed && NestedFault(s))
            throw new FaultBlocked();
        return allowed;
    }

    internal static bool HandleFault(FaultScope s, Exception ex)
    {
        if (s.Telemetry)
        {
            Instance?.WarnFaultSink(ex);
            return true;
        }
        if (ex is FaultBlocked)
            return !NestedFault(s);
        if (faultCleaning)
        {
            faultCleanupFailed = true;
            faultCleanupError ??= ex;
            return true;
        }
        if (!ex.Data.Contains(faultOriginKey))
            ex.Data[faultOriginKey] = new FaultOrigin(s);
        if (NestedFault(s))
            return false;
        var origin = ((FaultOrigin)ex.Data[faultOriginKey]!).Scope;
        Instance?.QuarantineFault(origin, ex);
        return true;
    }

    private void QuarantineFault(FaultScope s, Exception ex)
    {
        // A fail-safe must not itself escape into SAIN, Unity, or another bot's update.
        try
        {
            var b = s.Bot;
            if (!Authority || (b != null && !NativeAdapter.IsSupportedAi(b)))
                return;
            faultCleaning = true;
            faultCleanupFailed = false;
            faultCleanupError = null;
            try
            {
                if (b != null && states.TryGetValue(b, out var state))
                    CleanupFaultBot(b, state);
                else if (b == null)
                    CleanupFaultService(s.Module);
            }
            catch (Exception cleanup)
            {
                faultCleanupFailed = true;
                faultCleanupError ??= cleanup;
            }
            finally
            {
                faultCleaning = false;
            }
            bool failed = faultCleanupFailed || s.Cleanup;
            var t = faultIsolation.Fail(
                (object?)b ?? globalFaultKey,
                s.Module,
                s.Entry,
                ex,
                Time.unscaledTimeAsDouble,
                config?.FaultIsolation ?? defaultFaultConfig,
                failed
            );
            if (faultCleanupError != null)
            {
                t.CleanupError =
                    faultCleanupError.GetType().Name + ": " + faultCleanupError.Message;
                if (t.CleanupError.Length > 512)
                    t.CleanupError = t.CleanupError.Substring(0, 512);
            }
            if (b != null && !failed)
                pendingFaultReset.Add(b);
            RecordFault(b, t);
            SafeFaultLog(
                t.First
                    ? "C08 "
                        + s.Module
                        + " / "
                        + s.Entry
                        + ": "
                        + ex
                        + (faultCleanupError != null ? "\nCleanup: " + faultCleanupError : "")
                    : "C08 "
                        + s.Module
                        + ": "
                        + t.State
                        + " ("
                        + t.Count
                        + " in window, "
                        + t.Total
                        + " total). "
                        + t.Error
            );
        }
        catch (Exception secondary)
        {
            if (s.Bot != null)
                faultIsolation.DisableBot(s.Bot);
            SafeFaultLog(
                "C08 failed closed for "
                    + s.Module
                    + ": "
                    + secondary.GetType().Name
                    + " "
                    + secondary.Message
            );
        }
        finally
        {
            faultCleaning = false;
        }
    }

    private void CleanupFaultBot(BotComponent b, BotState s)
    {
        // Each operation has its own boundary. One bad cleanup cannot skip the rest.
        FaultCleanup(() => Release(s, "fault-error-ended"));
        FaultCleanup(() => EndCrossfire(b, s, "fault-error-ended"));
        FaultCleanup(() => EndPostCombat(b, s, "fault-error-ended", true));
        FaultCleanup(() => EndReadyStance(s, "fault-error-ended"));
        FaultCleanup(() => EndMedicalPosture(s, "fault-error-ended"));
        FaultCleanup(() => EndMedical(s, "fault-error-ended"));
        FaultCleanup(() => EndMovementRecovery(s, "fault-error-ended"));
        FaultCleanup(() => DropGrenadeEscape(b, s, "fault-error-ended"));
        FaultCleanup(() => DropOwnSafety(b, s, "fault-error-ended"));
        FaultCleanup(() => DropGas(b, s, "fault-error-ended"));
        FaultCleanup(() => DropFlash(b, s, "fault-error-ended", false));
        FaultCleanup(() => StopGunfire(s, "fault-error-ended"));
        FaultCleanup(() => StopQuiet(b, s, "fault-error-ended"));
        FaultCleanup(() => EndImpact(b, s, "fault-error-ended"));
        FaultCleanup(() => ReleaseThreat(b, s, "fault-error-ended"));
        FaultCleanup(() => DisposeDoorFake(s, "fault-error-ended"));
        if (!faultInsideCoverScan)
            FaultCleanup(() =>
            {
                if (b.Cover?.CoverFinder != null)
                    StopCoverScan(b.Cover.CoverFinder);
            });
        s.ActiveLabel = "";
        s.RequestedFrame = -1;
        s.Decision = "";
        Api.Decisions.Set(s.ProfileId, "");
        Api.Decisions.SetCrossfire(s.ProfileId, "");
        Api.Decisions.SetPostCombat(s.ProfileId, "");
        motionWatches.Remove(b);
    }

    private void CleanupFaultService(string module)
    {
        switch (module)
        {
            case "DoorMotion":
                FaultCleanup(ClearDoorMotion);
                break;
            case "DoorClosing":
                FaultCleanup(ClearDoorClosing);
                break;
            case "CoverScan":
                FaultCleanup(EndCoverScans);
                break;
            case "DoorSupport":
                FaultCleanup(() => ClearDoorSupports("fault-error-ended"));
                break;
            case "ExplosionAwareness":
                FaultCleanup(ClearBlasts);
                break;
            case "MovementHearing":
                FaultCleanup(() => ClearMovementHearing(true));
                break;
        }
    }

    private static void FaultCleanup(Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception ex)
        {
            faultCleanupFailed = true;
            faultCleanupError ??= ex;
        }
    }

    private void ResetFaultedBots()
    {
        if (pendingFaultReset.Count == 0)
            return;
        foreach (var b in pendingFaultReset)
        {
            bool live = true;
            GuardFaultReset(b, ref live);
            if (!live)
                continue;
            if (
                config == null
                || faultIsolation.BotDisabled(b)
                || !states.TryGetValue(b, out var previous)
            )
                continue;
            try
            {
                states[b] = new BotState(b, config, session, previous.Id);
            }
            catch (Exception ex)
            {
                var trace = faultIsolation.Fail(
                    b,
                    "StateReset",
                    "BotState",
                    ex,
                    Time.unscaledTimeAsDouble,
                    config.FaultIsolation,
                    true
                );
                trace.Reason = "fault-state-reset-failed";
                RecordFault(b, trace);
                SafeFaultLog("C08 fresh bot state failed: " + ex);
            }
        }
        pendingFaultReset.Clear();
    }

    private void RecordFault(BotComponent? bot, FaultTrace trace)
    {
        try
        {
            BotState? s = null;
            if (bot != null)
                states.TryGetValue(bot, out s);
            var json = Diagnostics.Serialize(
                new
                {
                    schema = 1,
                    session,
                    sequence = ++sequence,
                    feature = "C08",
                    stage = "evaluated",
                    decision = "FaultIsolation",
                    reason = trace.Reason,
                    realtime = Time.realtimeSinceStartup,
                    utc = DateTime.UtcNow.ToString("O"),
                    configRevision = config?.Revision ?? 0,
                    profileId = s?.ProfileId ?? "",
                    name = s?.Name ?? "COMET service",
                    botInstance = s?.Id ?? "",
                    faultIsolation = trace,
                    faultIsolationConfig = config?.FaultIsolation,
                }
            );
            diagnostics?.WriteEssential(json);
            Api.Decisions.Publish(json, WarnFaultSink);
        }
        catch (Exception ex)
        {
            WarnFaultSink(ex);
        }
    }

    private void WarnFaultSink(Exception ex)
    {
        if (faultSinkWarned)
            return;
        faultSinkWarned = true;
        SafeFaultLog("C08 diagnostics unavailable: " + ex.GetType().Name + " " + ex.Message);
    }

    private void SafeFaultLog(string text)
    {
        try
        {
            Logger.LogWarning(text);
        }
        catch
        { /* No error reporting recursion. */
        }
    }

    internal static BotComponent? FaultOwner(object? source)
    {
        for (int depth = 0; source != null && depth < 4; depth++)
        {
            if (source is BotComponent bot)
                return bot;
            if (source is BotState state)
                return state.Owner;
            if (source is EFT.BotOwner owner)
                return owner.GetComponent<BotComponent>();
            if (source is EFT.Player player)
            {
                if (Instance != null)
                    foreach (var pair in Instance.states)
                        if (ReferenceEquals(pair.Key.Player, player))
                            return pair.Key;
                return null;
            }
            var type = source.GetType();
            var getter = FaultOwnerAccessor(type);
            var next = getter(source);
            if (ReferenceEquals(next, source))
                return null;
            source = next;
        }
        return null;
    }

    private static Func<object, object?> FaultOwnerAccessor(Type type)
    {
        if (faultOwners.TryGetValue(type, out var getter))
            return getter;
        const BindingFlags flags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        MemberInfo? member = null;
        foreach (
            var name in new[]
            {
                "Bot",
                "BotComponent",
                "Owner",
                "_owner",
                "BotOwner",
                "Enemy",
                "_enemy",
                "Player",
            }
        )
        {
            for (var t = type; t != null && member == null; t = t.BaseType)
                member = (MemberInfo?)t.GetProperty(name, flags | BindingFlags.DeclaredOnly)
                    ?? t.GetField(name, flags | BindingFlags.DeclaredOnly);
            if (member != null)
                break;
        }
        if (member == null)
            getter = _ => null;
        else
        {
            var value = Expression.Parameter(typeof(object));
            var access = Expression.MakeMemberAccess(Expression.Convert(value, type), member);
            getter = Expression
                .Lambda<Func<object, object?>>(Expression.Convert(access, typeof(object)), value)
                .Compile();
        }

        faultOwners.Add(type, getter);
        return getter;
    }

    private static void PrepareFaultOwners()
    {
        foreach (
            var method in typeof(Plugin).GetMethods(
                BindingFlags.Public
                    | BindingFlags.NonPublic
                    | BindingFlags.Static
                    | BindingFlags.Instance
            )
        )
        foreach (var parameter in method.GetParameters())
        {
            var type = parameter.ParameterType;
            if (type.IsByRef)
                type = type.GetElementType()!;
            // Several native bot classes (BotReload, BotWeaponManager, etc.)
            // have no namespace. Prepare their reflection accessors at startup
            // too, rather than compiling expressions on the first combat call.
            if (
                type.IsClass
                && (
                    type.Assembly == typeof(EFT.Player).Assembly
                    || type.Namespace?.StartsWith("SAIN") == true
                    || type.Namespace?.StartsWith("EFT") == true
                )
            )
                try
                {
                    FaultOwnerAccessor(type);
                }
                catch
                {
                    faultOwners[type] = _ => null;
                }
        }
    }
}
