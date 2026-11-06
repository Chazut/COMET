using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Comet.Core;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Models.Enums;
using SAIN.Components.CoverFinder;
using SAIN.SAINComponent.Classes.Mover;
using SAIN.SAINComponent.SubComponents.CoverFinder;
using UnityEngine;

namespace Comet.Client;

internal sealed class CoverScanRuntime
{
    internal CoverScanRunner<CoverPoint> Runner = null!;
    internal readonly string Id = Guid.NewGuid().ToString("N");
    internal double LogAt = -100;
    internal string LastState = "", LastReason = "";
    internal int Frame = -1;
    internal string CheckedContact = "";
    internal bool ValidationFault;
    internal readonly Dictionary<CoverPoint, bool> Checked = new();
}
public sealed partial class Plugin
{
    partial void CoverScanFailure(BotComponent b, Exception ex, ref bool handled);
    partial void ConfigureCoverRetry(BotComponent b, ref Func<bool>? retry);
    private readonly Dictionary<CoverFinderComponent, CoverScanRuntime> coverScans = new();
    [ThreadStatic] private static CoverFinderComponent? scanningFinder;
    [ThreadStatic] private static CoverScanRunner<CoverPoint>? scanningRunner;
    [ThreadStatic] private static bool coverFactoryBypass;
    private static readonly MethodInfo coverFactory = AccessTools.Method(typeof(CoverFinderComponent), "FindCoverLoop");
    private static readonly MethodInfo coverAnalyzerGetter = AccessTools.PropertyGetter(typeof(CoverFinderComponent), "CoverAnalyzer");
    private void PatchCoverScan(Harmony h)
    {
        var moveNext = AccessTools.EnumeratorMoveNext(coverFactory);
        if (moveNext == null || coverAnalyzerGetter == null) throw new MissingMethodException("C07 SAIN cover scan contract");
        h.Patch(moveNext, transpiler: new HarmonyMethod(typeof(Plugin), nameof(RedirectCoverScan)));
        h.Patch(coverFactory, postfix: new HarmonyMethod(typeof(Plugin), nameof(WrapCoverScan)));
        h.Patch(AccessTools.Method(typeof(CoverFinderComponent), "StopLooking"), prefix: new HarmonyMethod(typeof(Plugin), nameof(StopCoverScan)));
        h.Patch(AccessTools.Method(typeof(SAINMoverClass), "GoToCoverPoint"), prefix: new HarmonyMethod(typeof(Plugin), nameof(ValidateScanCover)));
    }
    internal static IEnumerable<CodeInstruction> RedirectCoverScan(IEnumerable<CodeInstruction> instructions)
    {
        var code = new List<CodeInstruction>(instructions); int count = 0;
        var getter = AccessTools.PropertyGetter(typeof(CoverFinderComponent), "CoverPoints");
        foreach (var i in code) if (i.Calls(getter)) { i.opcode = OpCodes.Call; i.operand = AccessTools.Method(typeof(Plugin), nameof(ScanCoverPoints)); count++; }
        if (count != 6) throw new InvalidOperationException("C07 unexpected SAIN scan list access count: " + count);
        return code;
    }
    internal static List<CoverPoint> ScanCoverPoints(CoverFinderComponent finder) =>
        ReferenceEquals(scanningFinder, finder) && scanningRunner != null ? scanningRunner.Working : finder.CoverPoints;
    private static IEnumerator FreshCoverScan(CoverFinderComponent finder)
    {
        coverFactoryBypass = true;
        try { return (IEnumerator)(coverFactory.Invoke(finder, null) ?? throw new InvalidOperationException("SAIN returned no cover iterator")); } finally { coverFactoryBypass = false; }
    }
    private int CoverScanMode(BotComponent b) => config?.Enabled != true || !config.CoverScan.Enabled || !Authority || !NativeAdapter.IsPmcAi(b) ? 0
        : !b.BotActive || b.IsDead || b.GameEnding || Ghost(b.ProfileId) ? 2 : 1;
    private static string CoverContact(BotComponent b) => b.GoalEnemy?.LastKnownPosition.HasValue == true ? b.GoalEnemy.EnemyProfileId : "";
    private static void WrapCoverScan(CoverFinderComponent __instance, ref IEnumerator __result)
    {
        var p = Instance;
        if (coverFactoryBypass || p == null || !NativeAdapter.IsPmcAi(__instance.Bot)) return;
        StopCoverScan(__instance);
        var f = __instance; var b = f.Bot; var r = new CoverScanRuntime();
        Func<bool>? retry=null;p.ConfigureCoverRetry(b,ref retry);
        r.Runner = new CoverScanRunner<CoverPoint>(__result, f.CoverPoints, () => FreshCoverScan(f), () => p.CoverScanMode(b),
            () => CoverContact(b), () => Time.timeAsDouble, () => p.config!.CoverScan.MaxScanSeconds,
            it => it.Current is WaitForSeconds && f.CurrentStatus == ECoverFinderStatus.None,
            (runner, enter) => { scanningFinder = enter ? f : null; scanningRunner = enter ? runner : null; },
            t => p.RecordCoverScan(b, r, t), ex => { bool handled=false;p.CoverScanFailure(b,ex,ref handled);if(!handled)p.Logger.LogWarning("C07 yielded to SAIN: "+ex.GetBaseException().Message); }, new WaitForSeconds(.1f), retry);
        p.coverScans.Add(f, r); __result = r.Runner;
    }
    private static void StopCoverScan(CoverFinderComponent __instance)
    {
        var p = Instance;
        if (p != null && p.coverScans.TryGetValue(__instance, out var r)) { p.coverScans.Remove(__instance); r.Runner.Dispose(); }
    }
    private void CheckCoverScans() { foreach (var r in coverScans.Values) r.Runner.CheckContext(); }
    private void EndCoverScans() { foreach (var r in coverScans.Values) r.Runner.Dispose(); coverScans.Clear(); }
    internal static bool RetainedCover(BotComponent b, CoverPoint point) => Instance is Plugin p && b.Cover?.CoverFinder is CoverFinderComponent f
        && p.coverScans.TryGetValue(f, out var r) && r.Runner.Retains(CoverContact(b)) && f.CoverPoints.Contains(point);
    private static bool ValidateScanCover(SAINMoverClass __instance, CoverPoint point, ref bool __result)
    {
        var p = Instance; var b = __instance.Bot;
        if (p == null || p.CoverScanMode(b) != 1 || !p.coverScans.TryGetValue(b.Cover.CoverFinder, out var r)) return true;
        try
        {
            if (r.ValidationFault) return true;
            r.Runner.CheckContext();
            string contact = CoverContact(b);
            if (r.Frame != Time.frameCount || r.CheckedContact != contact) { r.Checked.Clear(); r.Frame = Time.frameCount; r.CheckedContact = contact; }
            if (point == null) { __result = false; return false; }
            bool valid; string why = "cover-selected-rechecked";
            if (!r.Checked.TryGetValue(point, out valid))
            {
                var known = b.GoalEnemy?.LastKnownPosition;
                valid = known.HasValue && !point.Spotted && point.Collider != null;
                if (valid)
                {
                    var analyzer = (CoverAnalyzer)(coverAnalyzerGetter.Invoke(b.Cover.CoverFinder, null) ?? throw new InvalidOperationException("SAIN cover analyzer unavailable"));
                    valid = analyzer.RecheckCoverPoint(point, known!.Value, (known.Value - b.NavMeshPosition).normalized, b.NavMeshPosition, out why);
                }
                point.CoverData.IsBad = !valid; r.Checked[point] = valid;
            }
            r.Runner.Selection(valid, valid ? "cover-selected-rechecked" : "cover-selected-invalid-" + why, ProgressAdapter.CoverKey(point));
            if (!valid) { __result = false; return false; }
            return true;
        }
        catch (Exception ex)
        {
            r.ValidationFault = true;
            p.Logger.LogWarning("C07 selected cover check failed: " + ex.GetBaseException().Message);
            r.Runner.Selection(false, "cover-selected-check-unavailable", ProgressAdapter.CoverKey(point));
            __result = false; return false;
        }
    }
    private void RecordCoverScan(BotComponent b, CoverScanRuntime r, CoverScanTrace t)
    {
        // Normal short scans need no per-frame events. Summarize at most once per second,
        // except invalidation, timeout and rejected movement, which must remain visible.
        bool urgent = t.State == "ended" || t.State == "timeout" || t.State == "rejected";
        if (Time.timeAsDouble - r.LogAt < 1 && (!urgent || t.State == r.LastState && t.Reason == r.LastReason)) return;
        r.LogAt = Time.timeAsDouble; r.LastState = t.State; r.LastReason = t.Reason;
        string id = states.TryGetValue(b, out var s) ? s.Id : r.Id;
        var json = Diagnostics.Serialize(new { schema = 1, session, sequence = ++sequence, feature = "C07", stage = "evaluated",
            decision = "CoverScan", reason = t.Reason, realtime = Time.realtimeSinceStartup, utc = DateTime.UtcNow.ToString("O"),
            configRevision = config?.Revision ?? 0, profileId = b.ProfileId, name = b.Player.Profile.Nickname, botInstance = id,
            coverScan = t, coverScanConfig = config?.CoverScan });
        diagnostics?.Write(json); Api.Decisions.Publish(json, ex => { if (!subscriberWarned) { subscriberWarned = true; Logger.LogWarning(ex.Message); } });
    }
}
