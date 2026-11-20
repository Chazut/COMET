using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using SAIN.Components.BotComponentSpace.Classes.EnemyClasses;
using SAIN.Models.Structs;
using SAIN.Preset.Shared.Enums;
using UnityEngine;

namespace Comet.Client;

internal sealed class ContactHeardSound
{
    internal float At { get; private set; }
    private SAINSoundType type;
    private Vector3 position;

    internal void Record(SAINHearingReport report, float now)
    {
        At = now;
        type = report.soundType;
        position = report.position;
    }

    private bool IsStep => type is SAINSoundType.FootStep or SAINSoundType.Sprint;

    internal bool IsNearbyStep(float now, Vector3 listener, double maxAge, double range) =>
        IsStep && now - At < maxAge && (position - listener).sqrMagnitude < range * range;

    internal bool IsCloserStep(float now, Vector3 listener, Vector3 known) =>
        IsStep
        && now - At < 5f
        && Vector3.Distance(position, listener) < Vector3.Distance(known, listener);
}

public sealed partial class Plugin
{
    private readonly ConditionalWeakTable<EnemyHearing, ContactHeardSound> contactSounds = new();
    private bool contactSoundWarned;

    private static void PatchContactHearing(Harmony h)
    {
        // SetHeard calls this after its ignore gate and visible-position adjustment, before squad sharing.
        h.Patch(
            AccessTools.Method(typeof(EnemyHearing), nameof(EnemyHearing.UpdateHeardPosition),
                new[] { typeof(SAINHearingReport), typeof(float) }),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(ObserveContactSound))
        );
    }

    private static void ObserveContactSound(EnemyHearing __instance, SAINHearingReport report)
    {
        var p = Instance;
        if (p?.config?.Enabled != true
            || !(p.config.CoverFeint.Enabled || p.config.MedicalChoice.Enabled))
            return;
        try
        {
            if (!p.Authority || !NativeAdapter.IsSupportedAi(__instance.Bot))
                return;
            var sound = p.contactSounds.GetValue(__instance, static _ => new ContactHeardSound());
            sound.Record(report, Time.time);
        }
        catch (Exception ex)
        {
            if (!p.contactSoundWarned)
            {
                p.contactSoundWarned = true;
                p.Logger.LogWarning("H01/M14 could not record a hearing report: " + ex.Message);
            }
        }
    }
}
