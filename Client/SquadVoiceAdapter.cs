using System;
using System.Collections.Generic;
using System.Reflection;
using Comet.Core;
using EFT;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Components.PlayerComponentSpace;
using SAIN.SAINComponent.Classes.Talk;
using UnityEngine;

namespace Comet.Client;

internal sealed class SquadVoiceToken
{
    internal SquadVoiceTrace Trace = new();
    internal BotTalkPackage Expected;
    internal bool Handled;
}

internal sealed class SquadVoiceRuntime
{
    internal SquadVoiceToken? Cache,
        Delay;
    internal int Serial;
    internal bool Fault;
}

internal sealed class SquadVoiceCall
{
    internal SquadVoiceCall? Previous;
    internal SAINBotTalkClass Talk = null!;
    internal BotState State = null!;
    internal SquadVoiceToken? Token;
    internal bool Delayed,
        Selected;
    internal int PlayDepth;
    internal BotTalkPackage Expected;
    internal string Group = "";
}

public sealed partial class Plugin
{
    [ThreadStatic]
    private static string? voiceSource;

    [ThreadStatic]
    private static SquadVoiceCall? voiceRequest,
        voicePlayback,
        voiceEmission;
    private readonly SquadVoicePolicy voicePolicy = new();
    private readonly System.Random voiceRandom = new(); // Never consumes Unity/SAIN's random stream.
    private static readonly Dictionary<MethodBase, string> VoiceSources = new();
    private static readonly FieldInfo VoiceCache = AccessTools.Field(
        typeof(SAINBotTalkClass),
        "_botTalkPackage"
    );
    private static readonly FieldInfo VoiceDelay = AccessTools.Field(
        typeof(SAINBotTalkClass),
        "_talkDelayPackage"
    );
    private static readonly FieldInfo VoiceCacheTime = AccessTools.Field(
        typeof(SAINBotTalkClass),
        "_talkCacheTimer"
    );
    private static readonly FieldInfo VoiceDelayTime = AccessTools.Field(
        typeof(SAINBotTalkClass),
        "_talkDelayTimer"
    );
    private static readonly HashSet<EPhraseTrigger> SquadVoicePhrases = new()
    {
        EPhraseTrigger.OnEnemyGrenade,
        EPhraseTrigger.OnGrenade,
        EPhraseTrigger.FollowMe,
        EPhraseTrigger.Gogogo,
        EPhraseTrigger.Suppress,
        EPhraseTrigger.Covering,
        EPhraseTrigger.Going,
        EPhraseTrigger.Regroup,
        EPhraseTrigger.Roger,
        EPhraseTrigger.Negative,
        EPhraseTrigger.GetInCover,
        EPhraseTrigger.GetBack,
        EPhraseTrigger.HoldPosition,
        EPhraseTrigger.OnYourOwn,
        EPhraseTrigger.Repeat,
        EPhraseTrigger.Stop,
        EPhraseTrigger.Ready,
        EPhraseTrigger.OnPosition,
        EPhraseTrigger.CoverMe,
        EPhraseTrigger.LostVisual,
        EPhraseTrigger.OnLostVisual,
        EPhraseTrigger.Rat,
        EPhraseTrigger.OnFirstContact,
        EPhraseTrigger.OnRepeatedContact,
        EPhraseTrigger.OnEnemyConversation,
        EPhraseTrigger.OnEnemyShot,
        EPhraseTrigger.SniperPhrase,
        EPhraseTrigger.EnemyDown,
        EPhraseTrigger.GoodWork,
        EPhraseTrigger.OnFriendlyDown,
        EPhraseTrigger.LootBody,
        EPhraseTrigger.LootGeneric,
        EPhraseTrigger.OnLoot,
        EPhraseTrigger.CheckHim,
        EPhraseTrigger.OnWeaponReload,
        EPhraseTrigger.NeedMedkit,
        EPhraseTrigger.NeedSniper,
        EPhraseTrigger.NeedHelp,
        EPhraseTrigger.OnSix,
        EPhraseTrigger.LeftFlank,
        EPhraseTrigger.RightFlank,
        EPhraseTrigger.InTheFront,
        EPhraseTrigger.NoisePhrase,
        EPhraseTrigger.OpenDoor,
        EPhraseTrigger.OnFight,
        EPhraseTrigger.GoForward,
        EPhraseTrigger.UnderFire,
        EPhraseTrigger.NeedAmmo,
        EPhraseTrigger.OnOutOfAmmo,
    };

    private static void PatchSquadVoice(Harmony h)
    {
        void Scope(MethodBase method)
        {
            VoiceSources[method] = method.DeclaringType!.Name + "." + method.Name;
            h.Patch(
                method,
                prefix: new HarmonyMethod(typeof(Plugin), nameof(SquadVoiceScopeBegin))
                {
                    priority = Priority.Last,
                },
                finalizer: new HarmonyMethod(typeof(Plugin), nameof(SquadVoiceScopeEnd))
            );
        }
        // Explicit native producers: a matching phrase alone must never classify another mod's voice as ours.
        foreach (
            var name in new[]
            {
                "OnDecisionMade",
                "OnMemberMadeDecision",
                "TalkSelfDecision",
                "TalkSoloDecision",
                "LeaderMadeDecision",
                "ShallReportLostVisual",
                "EnemyConversation",
                "TalkEnemySniper",
                "onEnemyHealthChanged",
                "CheckEnemyContact",
                "OnEnemyDown",
                "friendlyDown",
                "OnLootBody",
                "allMembersSay",
                "UpdateLeaderCommand",
                "myHealthChanged",
                "ShallReportNeedHelp",
                "enemyHeard",
                "CheckIfLeaderShouldCommand",
                "checkLeaderTalk",
                "TalkEnemyLocation",
            }
        )
            Scope(
                AccessTools.Method(typeof(GroupTalk), name)
                    ?? throw new MissingMethodException("GroupTalk", name)
            );
        Scope(AccessTools.Method(typeof(SAINBotTalkClass), nameof(SAINBotTalkClass.GroupSay)));
        Scope(AccessTools.Method(typeof(SAIN.Layers.Combat.Solo.ThrowGrenadeAction), "Start"));
        Scope(
            AccessTools.Method(
                typeof(SAIN.SAINComponent.Classes.EnemyClasses.Enemy),
                "SetEnemyAsSniper"
            )
        );
        foreach (var name in new[] { "Say", "TalkAfterDelay" })
            h.Patch(
                AccessTools.Method(typeof(SAINBotTalkClass), name),
                prefix: new HarmonyMethod(typeof(Plugin), nameof(SquadVoiceRequestBegin))
                {
                    priority = Priority.Last,
                },
                finalizer: new HarmonyMethod(typeof(Plugin), nameof(SquadVoiceRequestEnd))
            );
        foreach (var method in AccessTools.GetDeclaredMethods(typeof(SAINBotTalkClass)))
            if (method.Name == "CheckPriority")
                h.Patch(
                    method,
                    postfix: new HarmonyMethod(typeof(Plugin), nameof(SquadVoicePriority))
                );
        h.Patch(
            AccessTools.Method(typeof(SAINBotTalkClass), "checkTalk"),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(SquadVoicePlaybackBegin))
            {
                priority = Priority.Last,
            },
            finalizer: new HarmonyMethod(typeof(Plugin), nameof(SquadVoicePlaybackEnd))
        );
        h.Patch(
            AccessTools.Method(
                typeof(SAINBotTalkClass),
                "tellSpeakerToSay",
                new[] { typeof(EPhraseTrigger), typeof(ETagStatus), typeof(bool) }
            ),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(SquadVoiceEmitBegin))
            {
                priority = Priority.Last,
            },
            finalizer: new HarmonyMethod(typeof(Plugin), nameof(SquadVoiceEmitEnd))
        );
        h.Patch(
            AccessTools.Method(typeof(PlayerComponent), nameof(PlayerComponent.PlayVoiceLine)),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(SquadVoicePlayBegin)),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(SquadVoicePlayed)),
            finalizer: new HarmonyMethod(typeof(Plugin), nameof(SquadVoicePlayEnd))
        );
        h.Patch(
            AccessTools.Method(typeof(SAINBotTalkClass), nameof(SAINBotTalkClass.Dispose)),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(SquadVoiceDispose))
        );
    }

    private static void SquadVoiceScopeBegin(
        MethodBase __originalMethod,
        bool __runOriginal,
        out string? __state
    )
    {
        __state = voiceSource;
        if (__runOriginal && (voiceSource == null || __originalMethod.Name != "GroupSay"))
            voiceSource = VoiceSources[__originalMethod];
    }

    private static void SquadVoiceScopeEnd(string? __state) => voiceSource = __state;

    private bool SquadVoiceScope(BotComponent b) =>
        config?.Enabled == true
        && Authority
        && world != null
        && NativeAdapter.IsSupportedAi(b)
        && b.BotActive
        && !b.IsDead
        && !b.GameEnding
        && !Ghost(b.ProfileId)
        && b.Squad.BotInGroup
        && b.Squad.Members != null
        && b.Squad.Members.Count > 1
        && b.Squad.Members.Count <= 16;

    private static void SquadVoiceRequestBegin(
        SAINBotTalkClass __instance,
        EPhraseTrigger phrase,
        MethodBase __originalMethod,
        bool __runOriginal,
        out SquadVoiceCall? __state
    )
    {
        __state = null;
        var p = Instance;
        if (p == null || !__runOriginal)
            return;
        try
        {
            var b = __instance.Bot;
            if (b == null)
                return;
            bool eligible = p.SquadVoiceScope(b);
            bool candidate = eligible && voiceSource != null && SquadVoicePhrases.Contains(phrase);
            if (!p.states.TryGetValue(b, out var s))
            {
                if (!candidate)
                    return;
                s = new BotState(b, p.config!, p.session);
                p.states.Add(b, s);
            }
            var call = new SquadVoiceCall
            {
                Previous = voiceRequest,
                Talk = __instance,
                State = s,
                Delayed = __originalMethod.Name == "TalkAfterDelay",
            };
            if (candidate && !s.SquadVoice.Fault)
            {
                var c = p.config!.SquadVoice;
                call.Token = new()
                {
                    Trace = new()
                    {
                        Request = s.Id + ":voice:" + ++s.SquadVoice.Serial,
                        Phrase = phrase.ToString(),
                        Source = voiceSource!,
                        Mode = c.Mode,
                        Chance = c.Chance,
                        Urgent =
                            phrase == EPhraseTrigger.OnEnemyGrenade
                            || phrase == EPhraseTrigger.OnGrenade,
                        Action = b.Decision.CurrentCombatDecision.ToString(),
                        Alternatives = new[]
                        {
                            "Normal: retain SAIN's voice rules",
                            "Reduced: ordinary chance and spacing; urgent alerts bypass",
                            "Disabled: mute squad callouts only",
                        },
                    },
                };
                p.RecordSquadVoice(s, call.Token.Trace);
            }
            __state = voiceRequest = call;
        }
        catch (Exception ex)
        {
            p.VoiceError(__instance.Bot, ex);
        }
    }

    private static void SquadVoicePriority(
        SAINBotTalkClass __instance,
        BotTalkPackage? newTalk,
        BotTalkPackage? oldTalk,
        BotTalkPackage? __result
    )
    {
        var call = voiceRequest;
        if (
            call == null
            || !ReferenceEquals(call.Talk, __instance)
            || !newTalk.HasValue
            || !__result.HasValue
        )
            return;
        // Equal priority retains the OLD request, including identical phrase/mask requests from another source.
        if (
            (
                !oldTalk.HasValue
                || newTalk.Value.phraseInfo.Priority > oldTalk.Value.phraseInfo.Priority
            ) && __result.Value.Equals(newTalk.Value)
        )
        {
            call.Selected = true;
            call.Expected = __result.Value;
        }
    }

    private static void SquadVoiceRequestEnd(SquadVoiceCall? __state, Exception? __exception)
    {
        if (__state == null)
            return;
        voiceRequest = __state.Previous;
        var p = Instance;
        if (p == null)
            return;
        try
        {
            var r = __state.State.SquadVoice;
            var token = __state.Token;
            if (__exception != null)
            {
                p.VoiceError(__state.Talk.Bot, __exception);
                return;
            }
            if (__state.Selected)
            {
                var old = __state.Delayed ? r.Delay : r.Cache;
                if (old != null)
                    p.VoiceEnd(
                        __state.State,
                        old,
                        "superseded",
                        "squad-voice-native-priority-replaced"
                    );
                if (token != null)
                {
                    token.Expected = __state.Expected;
                    token.Trace.State = "queued";
                    token.Trace.Reason = "squad-voice-native-queued";
                    token.Trace.Execution = "queued by SAIN; no voice clip confirmed";
                    p.RecordSquadVoice(__state.State, token.Trace);
                }
                if (__state.Delayed)
                    r.Delay = token;
                else
                    r.Cache = token;
            }
            else if (token != null && !token.Handled)
                p.VoiceEnd(
                    __state.State,
                    token,
                    "native-declined",
                    "squad-voice-native-gate-or-priority"
                );
        }
        catch (Exception ex)
        {
            p.VoiceError(__state.Talk.Bot, ex);
        }
    }

    private static void SquadVoicePlaybackBegin(
        SAINBotTalkClass __instance,
        bool __runOriginal,
        out SquadVoiceCall? __state
    )
    {
        __state = null;
        var p = Instance;
        if (p == null || !__runOriginal || !p.states.TryGetValue(__instance.Bot, out var s))
            return;
        try
        {
            var r = s.SquadVoice;
            bool cache =
                (float)VoiceCacheTime.GetValue(__instance)! < Time.time
                && VoiceCache.GetValue(__instance) != null;
            bool delay =
                !cache
                && (float)VoiceDelayTime.GetValue(__instance)! < Time.time
                && VoiceDelay.GetValue(__instance) != null;
            var token =
                cache ? r.Cache
                : delay ? r.Delay
                : null;
            if (cache)
                r.Cache = null;
            if (delay)
                r.Delay = null;
            if (
                token != null
                && !(cache ? VoiceCache : VoiceDelay).GetValue(__instance)!.Equals(token.Expected)
            )
            {
                p.VoiceEnd(s, token, "released", "squad-voice-native-queue-changed");
                token = null;
            }
            __state = voicePlayback = new()
            {
                Previous = voicePlayback,
                Talk = __instance,
                State = s,
                Token = token,
            };
        }
        catch (Exception ex)
        {
            p.VoiceError(__instance.Bot, ex);
        }
    }

    private static void SquadVoicePlaybackEnd(SquadVoiceCall? __state, Exception? __exception)
    {
        if (__state == null)
            return;
        voicePlayback = __state.Previous;
        var p = Instance;
        if (p == null)
            return;
        if (__exception != null)
            p.VoiceError(__state.Talk.Bot, __exception);
        else if (__state.Token != null && !__state.Token.Handled)
            p.VoiceEnd(
                __state.State,
                __state.Token,
                "native-no-voice",
                "squad-voice-native-consumed-without-voice"
            );
    }

    private static bool SquadVoiceEmitBegin(
        SAINBotTalkClass __instance,
        EPhraseTrigger trigger,
        bool __runOriginal,
        out SquadVoiceCall? __state
    )
    {
        __state = null;
        var p = Instance;
        if (p == null || !__runOriginal)
            return true;
        try
        {
            // A direct Say call shadows a queued call, even if its request is protected/untracked.
            var call =
                ReferenceEquals(voiceRequest?.Talk, __instance) ? voiceRequest
                : ReferenceEquals(voicePlayback?.Talk, __instance) ? voicePlayback
                : null;
            var token = call?.Token;
            // Shadow an outer emission too: another mod's nested PlayVoiceLine must not be attributed to it.
            __state = voiceEmission = new()
            {
                Previous = voiceEmission,
                Talk = __instance,
                State = call?.State!,
                Token = null,
            };
            if (token == null || token.Handled || token.Trace.Phrase != trigger.ToString())
                return true;
            token.Handled = true;
            if (!p.SquadVoiceScope(__instance.Bot) || call!.State.SquadVoice.Fault)
            {
                p.VoiceEnd(
                    call!.State,
                    token,
                    "released",
                    "squad-voice-ineligible-native-retained"
                );
                return true;
            }
            __state.State = call.State;
            __state.Token = token;
            __state.Group = string.IsNullOrEmpty(__instance.Bot.Squad.SquadInfo?.GUID)
                ? SquadRosterIdentity(__instance.Bot)
                : __instance.Bot.Squad.SquadInfo.GUID;
            token.Trace.Action = __instance.Bot.Decision.CurrentCombatDecision.ToString();
            if (
                !p.voicePolicy.Evaluate(
                    token.Trace,
                    p.config!.SquadVoice,
                    call.State.Id,
                    __state.Group,
                    Time.time,
                    () => p.voiceRandom.NextDouble() * 100
                )
            )
            {
                p.RecordSquadVoice(call.State, token.Trace);
                return false;
            }
            token.Trace.State = "allowed";
            token.Trace.Execution = "native voice attempt allowed; no clip confirmed yet";
            return true;
        }
        catch (Exception ex)
        {
            p.VoiceError(__instance.Bot, ex);
            return true;
        }
    }

    private static void SquadVoicePlayBegin(PlayerComponent __instance, out SquadVoiceCall? __state)
    {
        __state = voiceEmission;
        if (__state == null || !ReferenceEquals(__state.Talk.PlayerComponent, __instance))
        {
            __state = null;
            return;
        }
        __state.PlayDepth++;
    }

    private static void SquadVoicePlayEnd(SquadVoiceCall? __state)
    {
        if (__state != null)
            __state.PlayDepth--;
    }

    private static void SquadVoicePlayed(
        PlayerComponent __instance,
        EPhraseTrigger phrase,
        bool __result
    )
    {
        var call = voiceEmission;
        var p = Instance;
        var token = call?.Token;
        if (
            p == null
            || token == null
            || call!.PlayDepth != 1
            || token.Trace.Filtered
            || token.Trace.Phrase != phrase.ToString()
            || !ReferenceEquals(call!.Talk.PlayerComponent, __instance)
        )
            return;
        try
        {
            if (__result)
                p.voicePolicy.Played(token.Trace, call.State.Id, call.Group, Time.time);
            else
            {
                token.Trace.State = "native-not-played";
                token.Trace.Execution = "SAIN did not accept a voice clip";
            }
        }
        catch (Exception ex)
        {
            p.VoiceError(call.Talk.Bot, ex);
        }
    }

    private static void SquadVoiceEmitEnd(SquadVoiceCall? __state, Exception? __exception)
    {
        if (__state == null)
            return;
        voiceEmission = __state.Previous;
        var p = Instance;
        var t = __state.Token?.Trace;
        if (p == null || t == null || t.Filtered)
            return;
        if (__exception != null)
            p.VoiceError(__state.Talk.Bot, __exception);
        // Even if a later callback failed, an already accepted clip remains an observed fact.
        if (t.State == "allowed")
        {
            t.State = "native-no-voice";
            t.Execution = "no native playback result observed";
        }
        p.RecordSquadVoice(__state.State, t);
    }

    private static void SquadVoiceDispose(SAINBotTalkClass __instance)
    {
        var p = Instance;
        if (p != null && p.states.TryGetValue(__instance.Bot, out var s))
            p.EndSquadVoice(s, "squad-voice-disposed");
    }

    private void EndSquadVoice(BotState s, string reason)
    {
        var r = s.SquadVoice;
        if (r.Cache != null)
            VoiceEnd(s, r.Cache, "released", reason);
        if (r.Delay != null)
            VoiceEnd(s, r.Delay, "released", reason);
        r.Cache = r.Delay = null;
    }

    private void VoiceEnd(BotState s, SquadVoiceToken token, string state, string reason)
    {
        token.Handled = true;
        var t = token.Trace;
        t.State = state;
        t.Reason = reason;
        t.ValidFor = 0;
        t.Execution = "no native voice playback confirmed";
        RecordSquadVoice(s, t);
    }

    private void VoiceError(BotComponent b, Exception ex)
    {
        if (b == null || !states.TryGetValue(b, out var s))
            return;
        if (!s.SquadVoice.Fault)
            Logger.LogWarning("S09 yielded: " + ex.GetBaseException().Message);
        s.SquadVoice.Fault = true;
        EndSquadVoice(s, "squad-voice-adapter-error");
    }

    private void RecordSquadVoice(BotState s, SquadVoiceTrace t)
    {
        var json = Diagnostics.Serialize(
            new
            {
                schema = 1,
                session,
                sequence = ++sequence,
                feature = "S09",
                stage = "evaluated",
                decision = "SquadVoice",
                reason = t.Reason,
                realtime = Time.realtimeSinceStartup,
                utc = DateTime.UtcNow.ToString("O"),
                configRevision = config?.Revision ?? 0,
                profileId = s.ProfileId,
                name = s.Name,
                botInstance = s.Id,
                squadVoice = t,
                squadVoiceConfig = config?.SquadVoice,
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
