using System.Runtime.CompilerServices;
using Comet.Core;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using SAIN.Components;
using SAIN.SAINComponent.Classes.Talk;

namespace UnityEngine { public static class Time { public static float time=10,realtimeSinceStartup=10; } }
namespace EFT { public enum ETagStatus { None=0,Combat=4 } }
namespace SAIN.Components
{
    public class BotComponent
    {
        public string ProfileId="pmc"; public bool Pmc=true,IsAi=true,BotActive=true,IsDead,GameEnding;
        public SquadData Squad=new();public DecisionData Decision=new();public SAINBotTalkClass Talk;
        public BotComponent(){Talk=new(this);}
    }
    public class SquadData { public bool BotInGroup=true;public Dictionary<string,BotComponent> Members=new();public SquadInfoData SquadInfo=new(); }
    public class SquadInfoData { public string GUID="squad-one"; }
    public class DecisionData { public string CurrentCombatDecision="StandAndShoot"; }
}
namespace SAIN.Components.PlayerComponentSpace
{
    public class PlayerComponent
    {
        public bool Busy;public int Played;public Action? BeforePlay;
        [MethodImpl(MethodImplOptions.NoInlining)] public bool PlayVoiceLine(EPhraseTrigger phrase,EFT.ETagStatus mask,bool aggressive)
        { BeforePlay?.Invoke(); if(Busy)return false;Played++;return true; }
    }
}
namespace SAIN.SAINComponent.Classes.Talk
{
    public struct PhraseInfo { public EPhraseTrigger Phrase;public int Priority;public float TimeLastSaid; }
    public struct BotTalkPackage { public PhraseInfo phraseInfo;public EFT.ETagStatus Mask; }
    // Engine boundary double: preserves the inspected native queue selection, tie, consumption and gesture contracts.
    // Harmony patches are the actual production adapter, not manually invoked replicas of its callbacks.
    public class SAINBotTalkClass
    {
        public BotComponent Bot;public PlayerComponentSpacePlaceholder Unused=new();
        public SAIN.Components.PlayerComponentSpace.PlayerComponent PlayerComponent=new();public GroupTalk GroupTalk;
        private BotTalkPackage? _botTalkPackage,_talkDelayPackage;private float _talkCacheTimer,_talkDelayTimer;
        public bool Gate=true,Gesture,ThrowRequest,ThrowPlay;public int Priority=10,Gestures,Hearing;
        public SAINBotTalkClass(BotComponent b){Bot=b;GroupTalk=new(this);}
        [MethodImpl(MethodImplOptions.NoInlining)] public bool Say(EPhraseTrigger phrase,EFT.ETagStatus? additionalMask=null,bool withGroupDelay=false,bool skipCheck=false)
        {
            if(ThrowRequest)throw new InvalidOperationException("native request fault");if(!Gate)return false;
            if(skipCheck){tellSpeakerToSay(phrase,additionalMask??0);return true;}
            var value=new BotTalkPackage{phraseInfo=new(){Phrase=phrase,Priority=Priority},Mask=additionalMask??0};
            _botTalkPackage=CheckPriority(value,_botTalkPackage);_talkCacheTimer=UnityEngine.Time.time+.25f;return true;
        }
        [MethodImpl(MethodImplOptions.NoInlining)] public bool GroupSay(EPhraseTrigger phrase,EFT.ETagStatus? additionalMask=null,bool withGroupDelay=false,float chance=60)
        {return Gate&&Say(phrase,additionalMask,withGroupDelay);}
        [MethodImpl(MethodImplOptions.NoInlining)] public void TalkAfterDelay(EPhraseTrigger phrase,EFT.ETagStatus? mask=null,float delay=.5f)
        {if(!Gate)return;var value=new BotTalkPackage{phraseInfo=new(){Phrase=phrase,Priority=Priority},Mask=mask??0};_talkDelayPackage=CheckPriority(value,_talkDelayPackage,out var change);if(change)_talkDelayTimer=UnityEngine.Time.time+delay;}
        [MethodImpl(MethodImplOptions.NoInlining)] private BotTalkPackage? CheckPriority(BotTalkPackage? newTalk,BotTalkPackage? oldTalk)
        {return !oldTalk.HasValue?newTalk:!newTalk.HasValue||oldTalk.Value.phraseInfo.Priority>=newTalk.Value.phraseInfo.Priority?oldTalk:newTalk;}
        [MethodImpl(MethodImplOptions.NoInlining)] private BotTalkPackage? CheckPriority(BotTalkPackage? newTalk,BotTalkPackage? oldTalk,out bool ChangeTalk)
        {ChangeTalk=!oldTalk.HasValue||newTalk.HasValue&&newTalk.Value.phraseInfo.Priority>oldTalk.Value.phraseInfo.Priority;return ChangeTalk?newTalk:oldTalk;}
        [MethodImpl(MethodImplOptions.NoInlining)] private void checkTalk()
        {
            BotTalkPackage? package=null;
            if(_talkCacheTimer<UnityEngine.Time.time&&_botTalkPackage.HasValue){package=_botTalkPackage;_botTalkPackage=null;}
            else if(_talkDelayTimer<UnityEngine.Time.time&&_talkDelayPackage.HasValue){package=_talkDelayPackage;_talkDelayPackage=null;}
            if(!package.HasValue)return;
            if(Gesture&&(package.Value.phraseInfo.Phrase==EPhraseTrigger.Roger||package.Value.phraseInfo.Phrase==EPhraseTrigger.Negative)){Gestures++;return;}
            tellSpeakerToSay(package.Value.phraseInfo.Phrase,package.Value.Mask);
        }
        [MethodImpl(MethodImplOptions.NoInlining)] private void tellSpeakerToSay(EPhraseTrigger phrase,EFT.ETagStatus mask)=>tellSpeakerToSay(phrase,mask,mask==EFT.ETagStatus.Combat);
        [MethodImpl(MethodImplOptions.NoInlining)] private void tellSpeakerToSay(EPhraseTrigger trigger,EFT.ETagStatus mask,bool aggressive)
        {if(ThrowPlay)throw new InvalidOperationException("native playback fault");if(PlayerComponent.PlayVoiceLine(trigger,mask,aggressive))Hearing++;}
        [MethodImpl(MethodImplOptions.NoInlining)] public void Dispose(){ }
        public void Tick()=>checkTalk();
        public void ReplaceExternally(EPhraseTrigger phrase)=>_botTalkPackage=new(){phraseInfo=new(){Phrase=phrase,Priority=99}};
    }
    public class PlayerComponentSpacePlaceholder { }
    public partial class GroupTalk
    {
        public SAINBotTalkClass Talk; public Action? Request;
        public GroupTalk(SAINBotTalkClass t){Talk=t;}
    }
}
namespace SAIN.Layers.Combat.Solo
{
    public class ThrowGrenadeAction
    {public SAINBotTalkClass Talk=null!;[MethodImpl(MethodImplOptions.NoInlining)]public void Start()=>Talk.Say(EPhraseTrigger.OnGrenade);}
}
namespace SAIN.SAINComponent.Classes.EnemyClasses
{
    public class Enemy
    {public SAINBotTalkClass Talk=null!;[MethodImpl(MethodImplOptions.NoInlining)]public void SetEnemyAsSniper()=>Talk.TalkAfterDelay(EPhraseTrigger.SniperPhrase);}
}
namespace Comet.Api { public static class Decisions { public static void Publish(string json,Action<Exception> error){ } } }
namespace Comet.Client
{
    internal class BotState
    {
        internal string ProfileId,Name="Test PMC",Id;internal readonly SquadVoiceRuntime SquadVoice=new();
        internal BotState(BotComponent b,CometConfig c,string session){ProfileId=b.ProfileId;Id=session+":"+b.ProfileId;}
    }
    internal static class NativeAdapter { internal static bool IsPmcAi(BotComponent b)=>b.Pmc&&b.IsAi; }
    internal class Diagnostics
    {
        internal static readonly JsonSerializerSettings Json=new(){ContractResolver=new DefaultContractResolver{NamingStrategy=new CamelCaseNamingStrategy()}};
        internal List<string> Events=new();internal void Write(string json)=>Events.Add(json);
    }
    internal class Log { public void LogWarning(string message){ } }
    public sealed partial class Plugin
    {
        internal static Plugin? Instance;internal CometConfig? config=new(){Enabled=true};internal bool Authority=true;internal object? world=new();
        internal string session="voice-harness";internal int sequence;internal bool subscriberWarned;internal Log Logger=new();internal Diagnostics? diagnostics=new();
        internal Dictionary<BotComponent,BotState> states=new();internal HashSet<string> Ghosts=new();
        private bool Ghost(string id)=>Ghosts.Contains(id);
        private static string ManeuverGroup(BotComponent b)=>string.Join("|",b.Squad.Members.Keys.OrderBy(x=>x));
        internal Plugin(){Instance=this;}
        internal static void Patch(Harmony h)=>PatchSquadVoice(h);
        internal void End(){foreach(var s in states.Values)EndSquadVoice(s,"raid-ended");voicePolicy.Clear();states.Clear();voiceSource=null;voiceRequest=voicePlayback=voiceEmission=null;}
        internal static bool Clean=>voiceSource==null&&voiceRequest==null&&voicePlayback==null&&voiceEmission==null;
    }
}
