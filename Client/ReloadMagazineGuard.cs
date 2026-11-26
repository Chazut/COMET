using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using SAIN.SAINComponent.Classes.Decision;
using UnityEngine;

namespace Comet.Client;

public sealed partial class Plugin
{
    private static Func<SelfActionDecisionClass, BotOwner, BotReload, bool> sainTryReload = null!;
    [ThreadStatic] private static BotReload? sainReloadProbe;

    private static void PatchReloadMagazineGuard(Harmony h)
    {
        sainTryReload = AccessTools.MethodDelegate<Func<SelfActionDecisionClass, BotOwner, BotReload, bool>>(
            AccessTools.DeclaredMethod(typeof(SelfActionDecisionClass), "TryReload"));
        h.Patch(AccessTools.DeclaredMethod(typeof(SelfActionDecisionClass), "CheckDoReload"),
            transpiler: new HarmonyMethod(typeof(Plugin), nameof(KeepSainReloadDecision)));
        h.Patch(AccessTools.Method(typeof(BotReloadMagazine), nameof(BotReloadMagazine.CanReload),
            new[] { typeof(bool), typeof(Magazine).MakeByRefType(), typeof(List<Ammo>).MakeByRefType() }),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(CheckMagazineGain)));
    }

    private static void CheckMagazineGain(BotReloadMagazine __instance, ref Magazine foundMag,
        ref List<Ammo> ammoForInternalReload, ref bool __result)
    {
        if (!__result || foundMag == null || Instance is not { } p) return;
        try
        {
            if (__instance.ShootController?.Item is not Weapon weapon ||
                p.JournalState(__instance._player) is not { } s ||
                !ReloadMagazineGain.KeepCurrent(weapon, __instance._player.InventoryController, ref foundMag,
                    ref ammoForInternalReload, s.ReloadTiming.Magazines, s.ReloadTiming.Ammo, out int loaded, out int replacement))
                return;
            __result = false;
            s.ReloadTiming.KeptMagazine = true;
            if (ReferenceEquals(sainReloadProbe, __instance)) sainReloadProbe = null;
            if (p.config?.RecordWeaponJournal() == true && !s.Journal.Fault && Time.time >= s.ReloadTiming.NextKeepLog)
            {
                s.ReloadTiming.NextKeepLog = Time.time + 5f;
                try { p.JournalWrite(s, "W01", "held", "reload-kept-current-magazine",
                    weapon: new { weapon = weapon.Id.ToString(), loaded, replacement }, limited: true); }
                catch (Exception ex) { p.JournalError(s, ex); }
            }
        }
        catch (Exception ex)
        {
            if (!p.reloadGainFaultLogged)
            {
                p.reloadGainFaultLogged = true;
                p.Logger.LogWarning("Magazine gain check unavailable: " + ex.GetBaseException().Message);
            }
        }
    }

    private bool reloadGainFaultLogged;

    // SAIN interprets a failed reload near a visible enemy as an opportunity to
    // switch guns or draw melee. A deliberate hold is a third outcome at this one call site.
    private static int ProbeSainReload(SelfActionDecisionClass decision, BotOwner owner, BotReload reload)
    {
        var outer = sainReloadProbe;
        sainReloadProbe = reload;
        try
        {
            if (sainTryReload(decision, owner, reload)) return 1;
            return sainReloadProbe == null ? -1 : 0;
        }
        finally { sainReloadProbe = outer; }
    }

    private static IEnumerable<CodeInstruction> KeepSainReloadDecision(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
    {
        var original = AccessTools.DeclaredMethod(typeof(SelfActionDecisionClass), "TryReload");
        var code = new List<CodeInstruction>(instructions);
        int call = code.FindIndex(i => i.Calls(original));
        if (call < 0 || call + 3 >= code.Count || code.FindLastIndex(i => i.Calls(original)) != call ||
            (code[call + 1].opcode != OpCodes.Brfalse_S && code[call + 1].opcode != OpCodes.Brfalse) ||
            code[call + 2].opcode != OpCodes.Ldc_I4_1 || code[call + 3].opcode != OpCodes.Ret)
            throw new InvalidOperationException("Unexpected SAIN reload decision branches");
        var outcome = generator.DeclareLocal(typeof(int));
        code[call] = new CodeInstruction(code[call]) { opcode = OpCodes.Call, operand = AccessTools.Method(typeof(Plugin), nameof(ProbeSainReload)) };
        // Zero takes the original failure branch. Both other outcomes return here,
        // with a positive result only for an actual reload.
        code[call + 2] = new CodeInstruction(code[call + 2]) { opcode = OpCodes.Ldloc, operand = outcome };
        code.InsertRange(call + 3, new[] { new CodeInstruction(OpCodes.Ldc_I4_0), new CodeInstruction(OpCodes.Cgt) });
        code.InsertRange(call + 1, new[] { new CodeInstruction(OpCodes.Dup), new CodeInstruction(OpCodes.Stloc, outcome) });
        return code;
    }
}
