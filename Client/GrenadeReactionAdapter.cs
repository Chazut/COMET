using System;
using System.Collections.Generic;
using System.Linq;
using Comet.Core;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Components.PlayerComponentSpace;
using SAIN.Layers;
using SAIN.Models.Enums;
using SAIN.SAINComponent.Classes.Mover;
using SAIN.SAINComponent.Classes.WeaponFunction;
using SAIN.SAINComponent.SubComponents;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

internal sealed class GrenadeEscapeRuntime
{
    internal readonly List<OwnGrenadeEvidence> Dangers=new();
    internal readonly HashSet<string> Escaping=new(StringComparer.Ordinal);
    internal readonly List<Vector3> Candidates=new();
    internal readonly List<string> Alternatives=new();
    internal readonly List<GrenadeDispersionLease> DispersionPeers=new();
    internal GrenadeDispersionLease? DispersionLease;
    internal GrenadeDispersionTrace? DispersionTrace;
    internal float DispersionRenewAt,DispersionLogAt=-1000;
    internal GrenadeTrackerClass? Selected;
    internal OwnGrenadeEvidence? Hazard;
    internal GrenadeReactionTrace? Trace;
    internal IBotPathData? Path;
    internal NavMeshPath? Mesh;
    internal Vector3 Goal;
    internal DoorPoint? KnownContact;
    internal float Next,CheckAt,CommitUntil,RetryAt,PathCheckAt,LogAt=-1000;
    internal int Candidate,CoverCandidates;
    internal bool Fault,Prone,Handled;
    internal string Reaction="Scatter",Key="",Contact="",ContactSource="";
}
public sealed partial class Plugin
{
    private int escapeProbeFrame=-1,escapeProbes;
    private static void PatchGrenadeReaction(Harmony h)
    {
        var action=AccessTools.TypeByName("SAIN.Layers.Combat.Solo.AvoidGrenadeAction")??throw new MissingMemberException("G05 native grenade action");
        h.Patch(AccessTools.Method(action,"Update"),prefix:new HarmonyMethod(typeof(Plugin),nameof(EscapeAction)){priority=Priority.First});
        h.Patch(AccessTools.Method(action,"Stop"),postfix:new HarmonyMethod(typeof(Plugin),nameof(EscapeActionStopped)));
        h.Patch(AccessTools.Method(typeof(BotPathDataManual),nameof(BotPathDataManual.TickPath)),prefix:new HarmonyMethod(typeof(Plugin),nameof(EscapePathTick)){priority=Priority.First});
        h.Patch(AccessTools.Method(typeof(SAIN.Classes.PlayerMovementController),"SetTargetMoveDirection"),prefix:new HarmonyMethod(typeof(Plugin),nameof(EscapeMoveInput)){priority=Priority.First});
    }
    private bool EscapeScope(BotComponent b)=>config?.Enabled==true&&config.GrenadeReaction.Enabled&&NativeAdapter.IsPmcAi(b)
        &&!b.IsDead&&b.BotActive&&!b.GameEnding&&b.SAINLayersActive&&Authority&&!Ghost(b.ProfileId);
    // Called by the existing G04 postfix: a single writer arbitrates native danger selection.
    private bool SelectGrenadeEscape(GrenadeReactionClass native)
    {
        var b=native.Bot;
        if(!EscapeScope(b)){if(states.TryGetValue(b,out var old))DropGrenadeEscape(b,old,"grenade-out-of-scope");return false;}
        var s=GrenadeState(b);var r=s.GrenadeEscape;if(r.Fault)return false;
        try
        {
            if(Time.time>=r.Next)
            {
                r.Next=Time.time+(float)config!.GrenadeReaction.Refresh;
                r.Dangers.Clear();var live=new HashSet<string>();GrenadeTrackerClass? selected=null;OwnGrenadeEvidence? selectedHazard=null;double best=double.MaxValue;
                void Add(GrenadeTrackerClass? tracker,OwnGrenadeEvidence e,bool own)
                {
                    if(e.Exploded||e.Expired(Time.time)||own&&e.Owner==b.ProfileId&&e.Grace(Time.time,config.OwnGrenadeSafety))return;
                    r.Dangers.Add(e);string key=e.Owner+":"+e.Projectile;live.Add(key);
                    var safety=new OwnGrenadeSafetyConfig{ExitPadding=config.GrenadeReaction.ExitPadding};
                    bool danger=OwnGrenadeSafetyRules.Threat(e,DP(b.Position),r.Escaping.Contains(key),Time.time,safety,OwnSolidCover,false);
                    if(!danger){r.Escaping.Remove(key);return;}r.Escaping.Add(key);
                    double score=(e.Position-DP(b.Position)).Length/Math.Max(1,e.Radius);
                    if(tracker!=null&&score<best){best=score;selected=tracker;selectedHazard=e;}
                }
                foreach(var t in native.EnemyGrenadesList.Values)
                {
                    if(t?.Grenade==null||!t.CanReact||t.Grenade.WeaponSource is not ThrowWeap item||item.IsDummy||item.ThrowType.ToString() is "smoke_grenade" or "gas_grenade")continue;
                    double radius=ClearDangerRadius(item)-1+config.GrenadeReaction.DangerMargin;
                    if(!GrenadeChoiceRules.Finite(radius)||radius<=0||radius>150||!DP(t.DangerPoint).Finite)continue;
                    if(r.Dangers.Count>=64)throw new InvalidOperationException("known grenade limit");
                    Add(t,new(){Owner=t.Grenade.ProfileId,Item=item.Id.ToString(),Projectile=t.Grenade.Id,Position=DP(t.DangerPoint),Radius=radius},false);
                }
                if(config.OwnGrenadeSafety.Enabled)
                {
                    SampleOwnProjectiles();
                    foreach(var own in ownProjectiles)if(own.Informed.Contains(b.ProfileId))
                    {
                        var e=own.Evidence;if(r.Dangers.Any(x=>x.Owner==e.Owner&&x.Projectile==e.Projectile))continue;
                        Add(ReferenceEquals(s.OwnGrenade.Selected,own)?s.OwnGrenade.Tracker:null,e,true);
                    }
                }
                r.Escaping.RemoveWhere(x=>!live.Contains(x));
                if(!ReferenceEquals(r.Selected,selected)||r.Hazard!=null&&selectedHazard!=null&&(r.Hazard.Position-selectedHazard.Position).Length>.5)
                {r.Candidates.Clear();r.RetryAt=0;r.CheckAt=0;}
                r.Selected=selected;r.Hazard=selectedHazard;r.PathCheckAt=0;
                if(selected==null){StopEscapePath(b,r);r.Candidates.Clear();r.RetryAt=0;EndGrenadeTrace(s,"grenade-safe-or-native-track-ended");}
                else if(r.Trace==null||r.Trace.State=="ended")Release(s,"grenade-escape-priority");
            }
            // Gas/smoke behavior is outside G05; never overwrite a native reaction to it.
            if(native.DangerGrenade?.Grenade?.WeaponSource is ThrowWeap special&&special.ThrowType.ToString() is "gas_grenade" or "smoke_grenade")
            {DropGrenadeEscape(b,s,"grenade-special-native-priority");return false;}
            if(r.Selected?.Grenade==null){r.Selected=null;r.Hazard=null;StopEscapePath(b,r);EndGrenadeTrace(s,"grenade-native-track-ended");}
            r.Handled=true;
            ownDangerSetter.Invoke(native,new object?[]{r.Selected});
            // Tactical movement is supplied in AvoidGrenadeAction. Native safety selection stays non-aggressive.
            ownReactionSetter.Invoke(native,new object[]{r.Selected==null?EGrenadeReaction.None:EGrenadeReaction.Scatter});
            return true;
        }
        catch(Exception ex){EscapeError(b,s,ex);return false;}
    }
    private static bool EscapeAction(BotAction __instance)
    {
        var p=Instance;var b=__instance.Bot;
        if(p?.TryGasAction(b)==true)return false;
        if(p==null||!p.EscapeScope(b)||!p.states.TryGetValue(b,out var s)||s.GrenadeEscape.Fault||!s.GrenadeEscape.Handled)return true;
        try{p.UpdateGrenadeEscape(b,s);return false;}catch(Exception ex){p.EscapeError(b,s,ex);return true;}
    }
    private void UpdateGrenadeEscape(BotComponent b,BotState s)
    {
        var r=s.GrenadeEscape;var c=config!.GrenadeReaction;if(r.Hazard==null||r.Selected?.Grenade==null)return;
        if(Time.time<r.CheckAt)return;r.CheckAt=Time.time+(float)c.Refresh;
        if(r.Path!=null&&ReferenceEquals(r.Path,b.Mover.ActivePath)&&ReferenceEquals(r.Path.NavMeshPath,r.Mesh)&&Time.time<r.CommitUntil
            &&GrenadeReactionRules.DestinationSafe(DP(r.Goal),r.Dangers,c.ExitPadding,OwnSolidCover)&&EscapeRouteSafe(r,RemainingEscape(b,r.Path)))
        {RenewDispersion(b,s);RecordGrenadeEscape(b,s,"moving","grenade-safe-destination-held");return;}
        if(r.Path==null&&Time.time<r.RetryAt){RecordGrenadeEscape(b,s,"prone","grenade-no-safe-route");return;}
        StopEscapePath(b,r);
        if(r.Candidates.Count==0)PrepareEscapeCandidates(b,r);
        PrepareDispersion(b,r);
        if(escapeProbeFrame!=Time.frameCount){escapeProbeFrame=Time.frameCount;escapeProbes=0;}
        NavMeshPath? sharedRoute=null;Vector3 sharedGoal=default;int sharedCandidate=0;
        GrenadeDispersionAssessment? sharedAssessment=null;
        int perBot=0;
        while(r.Candidate<r.Candidates.Count&&perBot++<4&&escapeProbes<8)
        {
            escapeProbes++;var target=r.Candidates[r.Candidate++];string reject="";
            if(!NavMesh.SamplePosition(target,out var hit,2,NavMesh.AllAreas))reject="no-navmesh";
            else if(Vector3.Distance(b.Position,hit.position)<1)reject="no-displacement";
            else
            {
                var mesh=new NavMeshPath();
                if(!NavMesh.CalculatePath(b.Position,hit.position,NavMesh.AllAreas,mesh)||mesh.status!=NavMeshPathStatus.PathComplete||mesh.corners.Length<2
                    ||Vector3.Distance(mesh.corners[0],b.Position)>2||Vector3.Distance(mesh.corners[mesh.corners.Length-1],hit.position)>.5f)reject="incomplete-route";
                else if(!GrenadeReactionRules.DestinationSafe(DP(hit.position),r.Dangers,c.ExitPadding,OwnSolidCover))reject="destination-exposed";
                else if(!EscapeRouteSafe(r,mesh.corners.Select(DP).ToArray()))reject="route-crosses-danger";
                else
                {
                    var dispersion=AssessDispersion(r,hit.position);
                    if(dispersion.Conflict)
                    {
                        if(sharedAssessment==null||dispersion.Nearest>sharedAssessment.Nearest)
                        {sharedRoute=mesh;sharedGoal=hit.position;sharedCandidate=r.Candidate;sharedAssessment=dispersion;}
                        reject="reserved-near-"+dispersion.Peer+" ("+dispersion.Nearest.ToString("0.0",System.Globalization.CultureInfo.InvariantCulture)+" m)";
                    }
                    else if(StartEscapeRoute(b,s,mesh,hit.position,r.Candidate,dispersion,false))return;
                    else
                        reject="native-movement-refused";
                }
            }
            if(r.Alternatives.Count<8)r.Alternatives.Add("Candidate "+r.Candidate+": "+reject);
        }
        // Spacing never adds a wait: use the best safe fallback in this same callback, including budget exhaustion.
        if(sharedRoute!=null&&sharedAssessment!=null&&StartEscapeRoute(b,s,sharedRoute,sharedGoal,sharedCandidate,sharedAssessment,true))return;
        if(r.Candidate<r.Candidates.Count){RecordGrenadeEscape(b,s,"planning","grenade-search-budget-yield");return;}
        // No fixed fuse estimate: prone only after exhausting this bounded reachable escape search.
        b.Mover.Prone.SetProne(true);r.Prone=true;r.RetryAt=Time.time+.3f;r.Candidates.Clear();
        RecordGrenadeEscape(b,s,"prone","grenade-no-safe-route",true);
    }
    private bool StartEscapeRoute(BotComponent b,BotState s,NavMeshPath mesh,Vector3 goal,int candidate,GrenadeDispersionAssessment dispersion,bool shared)
    {
        var r=s.GrenadeEscape;
        b.Mover.Prone.SetProne(false);r.Prone=false;
        if(!b.Mover.RunToPointByWay(mesh,true,.5f,ESprintUrgency.High,false))return false;
        if(r.Reaction=="Relocate"&&candidate>r.CoverCandidates)
        {r.Reaction="Scatter";if(r.Alternatives.Count<8)r.Alternatives.Add("Relocate fallback: no validated cover destination");}
        if(r.Reaction=="Push"&&r.KnownContact.HasValue&&(DP(goal)-r.KnownContact.Value).Length>=(DP(b.Position)-r.KnownContact.Value).Length)
        {r.Reaction="Scatter";if(r.Alternatives.Count<8)r.Alternatives.Add("Push fallback: safe route does not approach known opponent");}
        r.Path=b.Mover.ActivePath;r.Mesh=mesh;r.Goal=goal;r.CommitUntil=Time.time+(float)config!.GrenadeReaction.Commitment;r.Candidates.Clear();
        ReserveDispersion(b,s,dispersion,shared);
        RecordGrenadeEscape(b,s,"moving","grenade-safe-route-selected",true);return true;
    }
    private void PrepareEscapeCandidates(BotComponent b,GrenadeEscapeRuntime r)
    {
        r.Candidate=0;r.CoverCandidates=0;r.Alternatives.Clear();r.Contact=r.ContactSource="";DoorPoint? contact=null;
        var e=b.GoalEnemy;var known=e?.KnownPlaces.LastKnownPosition;
        if(e!=null&&known.HasValue&&GrenadeReactionRules.PersonalContact(e.IsVisible,RelocationContactSource(e),e.TimeSinceLastKnownUpdated))
        {contact=DP(known.Value);r.Contact=e.EnemyProfileId;r.ContactSource=RelocationContactSource(e);}
        r.KnownContact=contact;
        r.Reaction=GrenadeReactionRules.Preference(b.Info.Personality.ToString(),contact.HasValue);
        var direction=GrenadeReactionRules.Direction(DP(b.Position),r.Hazard!.Position,DP(b.LookDirection*-1),contact,r.Reaction);
        if(r.Reaction=="Push"&&contact.HasValue&&direction.Dot(contact.Value-DP(b.Position))<=0)
        {r.Reaction="Scatter";r.Alternatives.Add("Push rejected: opponent is toward the grenade");}
        if(r.Reaction=="Relocate")foreach(var cp in b.Cover.CoverPoints)
        {if(r.Candidates.Count>=8)break;if(cp!=null&&!cp.CoverData.IsBad&&Vector3.Distance(cp.Position,b.Position)>=2)r.Candidates.Add(cp.Position);}
        r.CoverCandidates=r.Candidates.Count;
        double distance=r.Reaction=="Retreat"?config!.GrenadeReaction.RetreatDistance:config!.GrenadeReaction.ScatterDistance;
        foreach(double scale in new[]{1d,.67,.34})foreach(float angle in new[]{0f,40f,-40f,80f,-80f,130f,-130f})
            r.Candidates.Add(b.Position+(Quaternion.Euler(0,angle,0)*VP(direction))*(float)(distance*scale));
    }
    private bool EscapeRouteSafe(GrenadeEscapeRuntime r,IReadOnlyList<DoorPoint> route)
    {
        var safety=new OwnGrenadeSafetyConfig{ExitPadding=config!.GrenadeReaction.ExitPadding};
        foreach(var e in r.Dangers)if(!OwnGrenadeSafetyRules.SafeRoute(e,route,Time.time,safety,OwnSolidCover,false))return false;
        return true;
    }
    private static List<DoorPoint> RemainingEscape(BotComponent b,IBotPathData path)
    {
        var points=new List<DoorPoint>{DP(b.Position)};
        for(int i=Math.Max(0,path.CurrentIndex);i<path.PathPoints.Count;i++){if(points.Count>=65)return new(){new(double.NaN,0,0),new(0,0,0)};points.Add(DP(path.PathPoints[i]));}
        return points;
    }
    private static bool EscapePathTick(BotPathDataManual __instance)
    {
        var p=Instance;if(p==null||__instance.PathFinder is not SAINMoverClass mover||!p.EscapeScope(mover.Bot)||!p.states.TryGetValue(mover.Bot,out var s))return true;
        var r=s.GrenadeEscape;if(r.Fault||!r.Handled||r.Dangers.Count==0||!ReferenceEquals(mover.ActivePath,__instance))return true;
        try
        {
            if(ReferenceEquals(r.Path,__instance)&&Time.time<r.PathCheckAt)return true;
            r.PathCheckAt=Time.time+(float)p.config!.GrenadeReaction.Refresh;
            if(p.EscapeRouteSafe(r,RemainingEscape(mover.Bot,__instance)))return true;
            combatStopPath.Invoke(__instance,new object[]{false,"COMET G05 grenade danger"});mover.Bot.Player.Move(Vector2.zero);
            p.ReleaseDispersion(mover.Bot,r,"dispersion-route-invalidated");
            r.Path=null;r.Mesh=null;r.CheckAt=0;r.Candidates.Clear();p.RecordGrenadeEscape(mover.Bot,s,"planning","grenade-route-invalidated");return false;
        }
        catch(Exception ex){p.EscapeError(mover.Bot,s,ex);return true;}
    }
    private static void EscapeMoveInput(PlayerComponent playerComp,ref Vector3 direction)
    {
        var p=Instance;var b=playerComp.BotComponent;if(p==null||b==null||direction.sqrMagnitude<.001f||!p.EscapeScope(b)||!p.states.TryGetValue(b,out var s))return;
        var r=s.GrenadeEscape;if(r.Fault||!r.Handled||r.Dangers.Count==0)return;
        try{if(!p.EscapeRouteSafe(r,new[]{DP(b.Position),DP(b.Position+direction.normalized*.75f)}))direction=Vector3.zero;}
        catch(Exception ex){p.EscapeError(b,s,ex);}
    }
    private static void EscapeActionStopped(BotAction __instance)
    {if(Instance is { } p&&p.states.TryGetValue(__instance.Bot,out var s)){p.StopEscapePath(__instance.Bot,s.GrenadeEscape);s.GrenadeEscape.Candidates.Clear();p.EndGrenadeTrace(s,"grenade-native-action-ended");}}
    private void StopEscapePath(BotComponent? b,GrenadeEscapeRuntime r)
    {
        ReleaseDispersion(b,r,"dispersion-route-released");
        if(b!=null&&r.Path!=null&&ReferenceEquals(b.Mover.ActivePath,r.Path)&&ReferenceEquals(r.Path.NavMeshPath,r.Mesh))
            combatStopPath.Invoke(r.Path,new object[]{false,"COMET G05 escape released"});
        if(b!=null&&r.Prone)b.Mover.Prone.SetProne(false);
        r.Prone=false;r.Path=null;r.Mesh=null;
    }
    private void DropGrenadeEscape(BotComponent? b,BotState s,string reason)
    {
        var r=s.GrenadeEscape;StopEscapePath(b,r);
        if(b!=null&&r.Selected!=null&&ReferenceEquals(b.Grenade.GrenadeReactionClass.DangerGrenade,r.Selected))
        {ownDangerSetter.Invoke(b.Grenade.GrenadeReactionClass,new object?[]{null});ownReactionSetter.Invoke(b.Grenade.GrenadeReactionClass,new object[]{EGrenadeReaction.None});}
        r.Selected=null;r.Hazard=null;r.Dangers.Clear();r.Escaping.Clear();r.Candidates.Clear();r.Handled=false;r.Next=0;r.RetryAt=0;EndGrenadeTrace(s,reason);
    }
    private void EscapeError(BotComponent b,BotState s,Exception ex)
    {if(!s.GrenadeEscape.Fault)Logger.LogWarning("G05 yielded to SAIN: "+ex.GetBaseException().Message);s.GrenadeEscape.Fault=true;DropGrenadeEscape(b,s,"grenade-adapter-error");}
    private void EndGrenadeTrace(BotState s,string reason)
    {if(s.GrenadeEscape.Trace is not { } t||t.State=="ended")return;t.State="ended";t.Reason=reason;t.ValidFor=0;WriteGrenadeTrace(s,true);}
    private void RecordGrenadeEscape(BotComponent b,BotState s,string state,string reason,bool force=false)
    {
        var r=s.GrenadeEscape;var e=r.Hazard;if(e==null)return;
        double length=0;var corners=r.Mesh?.corners;if(corners!=null)for(int i=1;i<corners.Length;i++)length+=Vector3.Distance(corners[i-1],corners[i]);
        r.Trace=new(){State=state,Reason=reason,Reaction=r.Reaction,Personality=b.Info.Personality.ToString(),Owner=e.Owner,Item=e.Item,Projectile=e.Projectile,
            Source=e.Owner==b.ProfileId?"own-throw":r.Selected==s.OwnGrenade.Tracker?"observed-squad-throw":"sain-detected",Active=r.Dangers.Count,
            X=e.Position.X,Y=e.Position.Y,Z=e.Position.Z,Radius=e.Radius,Distance=(DP(b.Position)-e.Position).Length,HasGoal=r.Path!=null,
            GoalX=r.Goal.x,GoalY=r.Goal.y,GoalZ=r.Goal.z,PathLength=length,Contact=r.Contact,ContactSource=r.ContactSource,Alternatives=r.Alternatives.ToArray()};WriteGrenadeTrace(s,force);
    }
    private void WriteGrenadeTrace(BotState s,bool force=false)
    {
        var r=s.GrenadeEscape;var t=r.Trace;if(t==null)return;string key=t.State+"|"+t.Reason+"|"+t.Owner+"|"+t.Projectile;
        if(!force&&key==r.Key&&Time.time-r.LogAt<.4f)return;r.Key=key;r.LogAt=Time.time;
        var json=JsonConvert.SerializeObject(new{schema=1,session,sequence=++sequence,feature="G05",stage="evaluated",decision="GrenadeReaction",reason=t.Reason,
            realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),configRevision=config?.Revision??0,profileId=s.ProfileId,name=s.Name,botInstance=s.Id,
            grenadeReaction=t,grenadeReactionConfig=config?.GrenadeReaction},Diagnostics.Json);
        diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>{if(!subscriberWarned){subscriberWarned=true;Logger.LogWarning(ex.Message);}});
    }
}
