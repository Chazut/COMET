using System;
using Comet.Core;
using EFT;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using UnityEngine;

namespace Comet.Client;

public sealed partial class Plugin
{
    private bool SourceSurgeryAllowed(BotComponent b, BotState s, bool retainedCover = false)
    {
        var med = b.BotOwner.Medecine;
        var kit = med.SurgicalKit;
        bool usable = !med.FirstAid.IsBleeding && (kit.Using || kit.ShallStartUse());
        string unavailable = "native-kit-not-ready";
        if (!usable)
            unavailable = med.FirstAid.IsBleeding ? "bleeding"
                : Time.time < kit._nextPosibleUseTime ? "kit-cooldown"
                : b.BotOwner.WeaponManager.Grenades.ThrowindNow ? "throwing-grenade"
                : !kit.HaveWork ? "no-native-surgery-work" : unavailable;
        bool visible = false;
        double nearest = double.MaxValue,
            seen = double.MaxValue,
            known = double.MaxValue;
        foreach (var e in b.EnemyController.KnownEnemies)
        {
            visible |= e.IsVisible;
            nearest = Math.Min(nearest, e.Path.PathLength);
            if (e.Seen)
                seen = Math.Min(seen, e.TimeSinceSeen);
            known = Math.Min(known, e.TimeSinceLastKnownUpdated);
        }
        bool strict = !visible && nearest >= 80 && seen >= 60 && known >= 60;
        return s.Medical.SourceSurgery.Allows(
            Time.time,
            b.Info.Personality.ToString(),
            kit.Using,
            usable,
            strict,
            b.Cover.CoverInUse?.BotInThisCover == true || retainedCover,
            visible,
            nearest,
            seen,
            known,
            b.Medical.TimeSinceShot,
            () => UnityEngine.Random.Range(0f, 100f),
            unavailable
        );
    }

    private void SampleSurgeryInterruption(BotComponent b, BotState s, bool force)
    {
        var r = s.Medical;
        var p = r.Interruption;
        var c = config!.SurgeryInterruption;
        if (!p.Active)
            return;
        if (!c.Enabled || !MedicalScope(b))
        {
            EndSurgeryInterruption(s, "surgery-out-of-scope");
            return;
        }
        var kit = b.BotOwner.Medecine.SurgicalKit;
        var t = p.Trace!;
        bool matching =
            b.Player.HandsController is IMedsController
            && b.Player.HandsController.Item?.Id.ToString() == t.Item;
        var f = new SurgeryFrame
        {
            Now = Time.time,
            NativeCancelled = kit._lastIsCanceled,
            Hands = matching,
            OperationPending = kit._using && !r.Callback,
            Completed = !b.Player.HealthController.IsBodyPartDestroyed(r.Part),
            WeaponReady = WeaponHandsReady(b, b.Player.HandsController as Player.FirearmController),
            SourceAllowed = SourceSurgeryAllowed(b, s),
            SourceCheck = r.SourceSurgery.LastCheck,
        };
        if (p.Tick(f, c))
            kit.CancelCurrent();
        if (p.Trace != null)
            p.Trace.NativeCancelled |= kit._lastIsCanceled;
        RecordSurgeryInterruption(s, false);
    }

    private void EndSurgeryInterruption(BotState s, string reason)
    {
        s.Medical.ExternalSurgery = false;
        s.Medical.ExternalSurgeryItem = "";
        s.Medical.ExternalSurgeryPart = null;
        if (!s.Medical.Interruption.Active)
            return;
        s.Medical.Interruption.End(reason);
        RecordSurgeryInterruption(s, true);
    }

    private void RecordSurgeryInterruption(BotState s, bool force)
    {
        var r = s.Medical;
        var t = r.Interruption.Trace;
        if (t == null)
            return;
        if (RecordingGestures && s.Gestures.Observe(t) is string gesture)
            PublishGesture(s, "H02", gesture);
        string key = t.State + ":" + t.Reason + ":" + t.Completed + ":" + t.NativeCancelled;
        if (!force && key == r.InterruptionKey && Time.time < r.InterruptionLogAt)
            return;
        r.InterruptionKey = key;
        r.InterruptionLogAt = Time.time + (t.Applied ? .5f : 4);
        if (!Comet.Core.ReplayCapture.Detailed)
            return;
        var json = Diagnostics.Serialize(
            new
            {
                schema = 1,
                session,
                sequence = ++sequence,
                feature = "H02",
                stage = "evaluated",
                decision = "SurgeryInterruption",
                reason = t.Reason,
                realtime = Time.realtimeSinceStartup,
                utc = DateTime.UtcNow.ToString("O"),
                configRevision = config?.Revision ?? 0,
                profileId = s.ProfileId,
                name = s.Name,
                botInstance = s.Id,
                surgeryInterruption = t,
                surgeryInterruptionConfig = config?.SurgeryInterruption,
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
