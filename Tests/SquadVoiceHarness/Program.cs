using Comet.Client;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using SAIN.Components;
using UnityEngine;

int checks=0;void C(bool b,string s){checks++;if(!b)throw new Exception("S09 adapter: "+s);}
var harmony=new Harmony("comet.s09.harness");Plugin.Patch(harmony);
var events=new List<string>();Plugin p=null!;BotComponent a=null!,b=null!;
void Setup(string mode="Reduced")
{
    if(p!=null){events.AddRange(p.diagnostics!.Events);p.End();}Time.time=Time.realtimeSinceStartup=10;
    p=new();p.config!.SquadVoice.Mode=mode;p.config.SquadVoice.Chance=100;
    a=new(){ProfileId="pmc-a"};b=new(){ProfileId="pmc-b"};a.Squad.Members[a.ProfileId]=a;a.Squad.Members[b.ProfileId]=b;b.Squad=a.Squad;
}
void Advance(float dt=.3f){Time.time+=dt;Time.realtimeSinceStartup=Time.time;}
void Group(Action call,BotComponent? bot=null){var t=(bot??a).Talk;t.GroupTalk.Request=call;t.GroupTalk.OnDecisionMade();C(Plugin.Clean,"group scope restored");}
void Speak(EPhraseTrigger phrase=EPhraseTrigger.OnFirstContact,BotComponent? bot=null){(bot??a).Talk.GroupSay(phrase);Advance();(bot??a).Talk.Tick();C(Plugin.Clean,"playback scope restored");}
JToken Last()=>JObject.Parse(p.diagnostics!.Events.Last())["squadVoice"]!;
Setup("Normal");Speak();C(a.Talk.PlayerComponent.Played==1&&a.Talk.Hearing==1,"normal native clip and hearing");C((string?)Last()["state"]=="native-played"&&!(bool)Last()["filtered"]!,"confirmed clip event");
Setup("Disabled");Speak();C(a.Talk.PlayerComponent.Played==0&&a.Talk.Hearing==0,"filtered before hearing and audio");C((string?)Last()["state"]=="filtered","mute telemetry");
foreach(var phrase in new[]{EPhraseTrigger.OnDeath,EPhraseTrigger.OnAgony,EPhraseTrigger.OnBeingHurt,EPhraseTrigger.HurtLight,EPhraseTrigger.HurtHeavy,EPhraseTrigger.HurtNearDeath,EPhraseTrigger.OnBreath,EPhraseTrigger.Provocation,EPhraseTrigger.MumblePhrase})
{int count=a.Talk.PlayerComponent.Played;Group(()=>a.Talk.Say(phrase,null,false,true));C(a.Talk.PlayerComponent.Played==count+1,"protected phrase "+phrase);}
int before=a.Talk.PlayerComponent.Played;a.Talk.Say(EPhraseTrigger.OnFirstContact,null,false,true);C(a.Talk.PlayerComponent.Played==before+1,"other mod direct Say untouched");
Group(()=>a.Talk.PlayerComponent.PlayVoiceLine(EPhraseTrigger.OnFirstContact,0,false));C(a.Talk.PlayerComponent.Played==before+2,"direct speaker call untouched even inside group producer");
Setup();Speak();Speak(bot:b);C(b.Talk.PlayerComponent.Played==0&&(string?)Last()["reason"]=="squad-voice-squad-delay","squad cooldown");Advance(2);Speak(bot:b);C(b.Talk.PlayerComponent.Played==1,"teammate after gap");Speak();C(a.Talk.PlayerComponent.Played==1&&(string?)Last()["reason"]=="squad-voice-bot-delay","bot cooldown");
Setup();p.config!.SquadVoice.Chance=0;Speak();C(a.Talk.PlayerComponent.Played==0&&(string?)Last()["reason"]=="squad-voice-chance","0% ordinary");Speak(EPhraseTrigger.OnEnemyGrenade);C(a.Talk.PlayerComponent.Played==1,"enemy grenade bypass");new SAIN.Layers.Combat.Solo.ThrowGrenadeAction{Talk=a.Talk}.Start();Advance();a.Talk.Tick();C(a.Talk.PlayerComponent.Played==2,"native own grenade producer urgent bypass");
p.config.SquadVoice.Mode="Disabled";Speak(EPhraseTrigger.OnEnemyGrenade);C(a.Talk.PlayerComponent.Played==2,"disabled includes urgent squad alerts");
Setup();a.Talk.PlayerComponent.Busy=true;Speak();C((string?)Last()["state"]=="native-not-played"&&(double)Last()["validFor"]! == 0,"native declined audio never confirmed");a.Talk.PlayerComponent.Busy=false;Speak();C(a.Talk.PlayerComponent.Played==1,"busy attempt did not consume spacing");
Setup("Disabled");a.Talk.Gesture=true;Speak(EPhraseTrigger.Roger);C(a.Talk.Gestures==1&&a.Talk.PlayerComponent.Played==0,"native gesture preserved in disabled");C((string?)Last()["state"]=="native-no-voice","gesture not falsely confirmed as sound");
Setup("Disabled");Group(()=>a.Talk.TalkAfterDelay(EPhraseTrigger.OnFirstContact,null,1));Advance(.6f);a.Talk.Tick();C((string?)Last()["state"]=="queued","not due");Advance(.5f);a.Talk.Tick();C((string?)Last()["state"]=="filtered"&&a.Talk.PlayerComponent.Played==0,"delayed origin retained");
Setup("Disabled");Group(()=>b.Talk.TalkAfterDelay(EPhraseTrigger.GoodWork));Advance(1);b.Talk.Tick();C(b.Talk.PlayerComponent.Played==0&&p.states[b].SquadVoice.Delay==null,"leader queues teammate response correctly");
Setup("Disabled");a.Talk.GroupSay(EPhraseTrigger.OnFirstContact);a.Talk.Say(EPhraseTrigger.OnFirstContact);Advance();a.Talk.Tick();C(a.Talk.PlayerComponent.Played==0,"equal priority direct request cannot steal original squad tag");
Setup("Disabled");a.Talk.Say(EPhraseTrigger.OnFirstContact);a.Talk.GroupSay(EPhraseTrigger.OnFirstContact);Advance();a.Talk.Tick();C(a.Talk.PlayerComponent.Played==1,"equal priority squad request cannot tag an existing direct voice");C(p.diagnostics!.Events.Any(e=>e.Contains("native-declined")),"lost priority visible in trace");
Setup("Disabled");a.Talk.GroupSay(EPhraseTrigger.OnFirstContact);a.Talk.Priority=30;a.Talk.Say(EPhraseTrigger.OnDeath);Advance();a.Talk.Tick();C(a.Talk.PlayerComponent.Played==1&&p.states[a].SquadVoice.Cache==null,"protected higher priority clears old tag");C(p.diagnostics!.Events.Any(e=>e.Contains("superseded")),"replacement traced");
Setup("Disabled");a.Talk.GroupSay(EPhraseTrigger.OnFirstContact);a.Talk.ReplaceExternally(EPhraseTrigger.OnGrenade);Advance();a.Talk.Tick();C(a.Talk.PlayerComponent.Played==1&&(string?)Last()["state"]=="released","external queue replacement fails open");
Setup("Disabled");Group(()=>a.Talk.TalkAfterDelay(EPhraseTrigger.OnFirstContact));a.Talk.GroupSay(EPhraseTrigger.OnGrenade);Advance(1);a.Talk.Tick();C(p.states[a].SquadVoice.Delay!=null&&p.states[a].SquadVoice.Cache==null,"cache slot consumed before delay slot");a.Talk.Tick();C(a.Talk.PlayerComponent.Played==0&&p.states[a].SquadVoice.Delay==null,"two independent queues");
foreach(var scope in new Action[]{()=>a.Pmc=false,()=>a.IsAi=false,()=>a.BotActive=false,()=>a.IsDead=true,()=>a.GameEnding=true,()=>p.Authority=false,()=>p.world=null,()=>p.config!.Enabled=false,()=>p.Ghosts.Add(a.ProfileId),()=>a.Squad.BotInGroup=false,()=>a.Squad.Members.Remove(b.ProfileId)})
{Setup("Disabled");scope();Speak();C(a.Talk.PlayerComponent.Played==1&&p.diagnostics!.Events.Count==0,"ineligible source untouched");}
Setup("Disabled");a.Talk.GroupSay(EPhraseTrigger.OnFirstContact);p.Ghosts.Add(a.ProfileId);Advance();a.Talk.Tick();C(a.Talk.PlayerComponent.Played==1&&(string?)Last()["state"]=="released","scope rechecked at playback");
Setup();a.Talk.ThrowRequest=true;bool thrown=false;try{Group(()=>a.Talk.Say(EPhraseTrigger.OnFirstContact));}catch(InvalidOperationException){thrown=true;}C(thrown&&Plugin.Clean,"native exception propagated; thread scopes restored");
Setup();a.Talk.ThrowPlay=true;a.Talk.GroupSay(EPhraseTrigger.OnFirstContact);Advance();thrown=false;try{a.Talk.Tick();}catch(InvalidOperationException){thrown=true;}C(thrown&&Plugin.Clean&&a.Talk.PlayerComponent.Played==0,"playback exception scopes restored without false audio");
Setup();a.Talk.GroupSay(EPhraseTrigger.OnFirstContact);a.Talk.Dispose();C(p.states[a].SquadVoice.Cache==null,"dispose clears tokens");
Setup();Speak();p.End();Speak();C(a.Talk.PlayerComponent.Played==2,"raid reset clears cooldown");
// A nested direct call from another mod cannot steal the outer native playback acknowledgement.
Setup();var nested=new SAIN.Components.PlayerComponentSpace.PlayerComponent();a.Talk.PlayerComponent.Busy=true;a.Talk.PlayerComponent.BeforePlay=()=>nested.PlayVoiceLine(EPhraseTrigger.OnFirstContact,0,false);Speak();C(nested.Played==1&&(string?)Last()["state"]=="native-not-played","different player nested voice is not our acknowledgement");
Setup();bool inside=false;var speaker=a.Talk.PlayerComponent;speaker.BeforePlay=()=>{if(inside)return;inside=true;speaker.Busy=false;speaker.PlayVoiceLine(EPhraseTrigger.OnFirstContact,0,false);speaker.Busy=true;};Speak();C(speaker.Played==1&&(string?)Last()["state"]=="native-not-played"&&!(bool)Last()["played"]!,"nested same-player direct clip does not confirm outer declined call");
// Export production events for RR parser, roundtrip and UI checks.
Setup();Time.time=Time.realtimeSinceStartup=22;Speak();Advance(.3f);Speak();events.AddRange(p.diagnostics!.Events);
foreach(var json in events){var r=JObject.Parse(json);var t=r["squadVoice"]!;C((string?)r["feature"]=="S09","event feature");C((double)t["validFor"]!<=2,"bounded map context");if((double)t["validFor"]!>0)C((bool)t["played"]!||(bool)t["filtered"]!,"map only confirms audio result or suppression");}
var output=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../artifacts/s09-adapter-events.jsonl"));File.WriteAllLines(output,events);
p.End();harmony.UnpatchSelf();C(Plugin.Clean,"final teardown");
Console.WriteLine($"S09 actual Harmony adapter: {checks} checks passed; {events.Count} events. Native engine boundaries are doubles; no in-game raid. {output}");
