using System;
using System.Collections.Generic;
using Comet.Core;
using Newtonsoft.Json;
using SAIN.Components;
using UnityEngine;

namespace Comet.Client;

public sealed partial class Plugin
{
    private long dispersionToken;
    private static string EscapeSquad(BotComponent b) => b.Squad?.SquadInfo?.GUID ?? "";

    private void PrepareDispersion(BotComponent b, GrenadeEscapeRuntime r)
    {
        r.DispersionPeers.Clear();
        if (config?.GrenadeDispersion.Enabled != true || string.IsNullOrEmpty(EscapeSquad(b))) return;
        string squad = EscapeSquad(b);
        int visited = 0;
        foreach (var peer in b.Squad.Members.Values)
        {
            if (++visited > 32) break;
            if (peer == null || ReferenceEquals(peer, b) || !EscapeScope(peer) || EscapeSquad(peer) != squad
                || !GrenadeDispersionRules.Nearby(DP(b.Position), DP(peer.Position), config.GrenadeDispersion.MemberRange)
                || !states.TryGetValue(peer, out var ps)) continue;
            var pr = ps.GrenadeEscape;
            var lease = pr.DispersionLease;
            if (lease == null || lease.Squad != squad || lease.Instance != ps.Id || lease.Profile != peer.ProfileId
                || lease.Expires <= Time.time || pr.Fault || !pr.Handled || pr.Selected?.Grenade == null || pr.Hazard == null
                || pr.Path == null || !ReferenceEquals(pr.Path, peer.Mover.ActivePath) || !ReferenceEquals(pr.Mesh, pr.Path.NavMeshPath)
                || pr.Trace?.State != "moving") continue;
            r.DispersionPeers.Add(lease);
        }
    }

    private GrenadeDispersionAssessment AssessDispersion(GrenadeEscapeRuntime r, Vector3 goal)
        => config?.GrenadeDispersion.Enabled == true
            ? GrenadeDispersionRules.Assess(DP(goal), r.DispersionPeers, config.GrenadeDispersion.Spacing, Time.time)
            : new GrenadeDispersionAssessment();

    private void ReserveDispersion(BotComponent b, BotState s, GrenadeDispersionAssessment assessment, bool shared)
    {
        var r = s.GrenadeEscape;
        if (config?.GrenadeDispersion.Enabled != true || string.IsNullOrEmpty(EscapeSquad(b))) return;
        r.DispersionLease = new()
        {
            Profile = b.ProfileId, Instance = s.Id, Squad = EscapeSquad(b), Token = ++dispersionToken,
            Goal = DP(r.Goal), Expires = Math.Min(Time.time + GrenadeDispersionRules.LeaseSeconds, r.CommitUntil)
        };
        r.DispersionRenewAt = Time.time + (float)GrenadeDispersionRules.RenewalSeconds;
        r.DispersionTrace = new()
        {
            State = shared ? "shared" : "reserved", Reason = shared ? "dispersion-shared-safe-exit" : "dispersion-separated-destination",
            Squad = r.DispersionLease.Squad, Token = r.DispersionLease.Token, Peer = assessment.Peer, Peers = assessment.Peers,
            X = r.Goal.x, Y = r.Goal.y, Z = r.Goal.z, Spacing = config.GrenadeDispersion.Spacing,
            MemberRange = config.GrenadeDispersion.MemberRange, Nearest = assessment.Nearest,
            LeaseRemaining = r.DispersionLease.Expires - Time.time, Alternatives = r.Alternatives.ToArray()
        };
        WriteDispersion(s, true);
    }

    private void RenewDispersion(BotComponent b, BotState s)
    {
        var r = s.GrenadeEscape; var lease = r.DispersionLease;
        if (lease == null) return;
        if (config?.GrenadeDispersion.Enabled != true || EscapeSquad(b) != lease.Squad || lease.Instance != s.Id
            || lease.Expires <= Time.time || r.Path == null || !ReferenceEquals(r.Path, b.Mover.ActivePath)
            || !ReferenceEquals(r.Mesh, r.Path.NavMeshPath))
        { ReleaseDispersion(b, r, "dispersion-reservation-ended"); return; }
        if (Time.time < r.DispersionRenewAt) return;
        // Called only by the active native escape action after G05 revalidates its route.
        lease.Expires = Math.Min(Time.time + GrenadeDispersionRules.LeaseSeconds, r.CommitUntil);
        r.DispersionRenewAt = Time.time + (float)GrenadeDispersionRules.RenewalSeconds;
        if (r.DispersionTrace != null) { r.DispersionTrace.LeaseRemaining = lease.Expires - Time.time; WriteDispersion(s); }
    }

    private void ReleaseDispersion(BotComponent? b, GrenadeEscapeRuntime r, string reason)
    {
        r.DispersionLease = null; r.DispersionPeers.Clear();
        if (r.DispersionTrace is not { } t || t.State == "ended") return;
        t.State = "ended"; t.Reason = reason; t.ValidFor = 0; t.LeaseRemaining = 0;
        if (b != null && states.TryGetValue(b, out var s)) WriteDispersion(s, true);
    }

    private void WriteDispersion(BotState s, bool force = false)
    {
        var r = s.GrenadeEscape; var t = r.DispersionTrace; if (t == null) return;
        if (!force && Time.time - r.DispersionLogAt < .4f) return; r.DispersionLogAt = Time.time;
        var json = JsonConvert.SerializeObject(new
        {
            schema = 1, session, sequence = ++sequence, feature = "G06", stage = "evaluated", decision = "GrenadeDispersion", reason = t.Reason,
            realtime = Time.realtimeSinceStartup, utc = DateTime.UtcNow.ToString("O"), configRevision = config?.Revision ?? 0,
            profileId = s.ProfileId, name = s.Name, botInstance = s.Id, grenadeDispersion = t, grenadeDispersionConfig = config?.GrenadeDispersion
        }, Diagnostics.Json);
        diagnostics?.Write(json); Api.Decisions.Publish(json, ex => { if (!subscriberWarned) { subscriberWarned = true; Logger.LogWarning(ex.Message); } });
    }
}
