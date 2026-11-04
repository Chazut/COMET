using Comet.Core;
using Newtonsoft.Json;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

internal static class SquadVoiceTests
{
    public static int Run()
    {
        int n=0; void C(bool b,string s){n++;if(!b)throw new Exception("S09: "+s);}
        var c=new SquadVoiceConfig();C(c.Mode=="Normal"&&c.Chance==50&&c.BotDelay==5&&c.SquadDelay==2,"defaults");
        foreach(var json in new[]{"{}","{\"SquadVoice\":null}"}){var old=JsonConvert.DeserializeObject<CometConfig>(json)!;old.Validate();C(old.SquadVoice.Mode=="Normal","old settings retain native voice");}
        foreach(var m in new Action<SquadVoiceConfig>[]{x=>x.Mode="quiet",x=>x.Mode=null!,x=>x.Chance=double.NaN,x=>x.Chance=101,x=>x.BotDelay=-1,x=>x.SquadDelay=double.PositiveInfinity})
        {var b=new SquadVoiceConfig();m(b);bool bad=false;try{b.Validate();}catch(ArgumentException){bad=true;}C(bad,"invalid settings rejected");}
        var p=new SquadVoicePolicy();SquadVoiceTrace T(bool urgent=false)=>new(){Urgent=urgent};int rolls=0;double Roll(){rolls++;return 20;}
        C(p.Evaluate(T(),c,"a","s",10,Roll)&&rolls==0,"normal consumes no new random draws");
        c.Mode="Reduced";
        var t=T();C(p.Evaluate(t,c,"a","s",10,Roll),"ordinary accepted");
        C(p.Evaluate(T(),c,"a","s",10,Roll),"unconfirmed attempt consumes no cooldown");p.Played(t,"a","s",10);
        C(t.Played&&!t.Filtered&&t.ValidFor==2,"confirmed native clip telemetry");
        t=T();C(!p.Evaluate(t,c,"a","s",11,Roll)&&t.BotWait==4&&t.Reason=="squad-voice-bot-delay","same bot spacing");
        t=T();C(!p.Evaluate(t,c,"b","s",11,Roll)&&t.SquadWait==1&&t.Reason=="squad-voice-squad-delay","teammate spacing");
        C(p.Evaluate(T(),c,"b","other",11,Roll),"independent squads");
        C(p.Evaluate(T(),c,"b","s",12,Roll),"exact squad boundary");
        C(p.Evaluate(T(),c,"a","s",15,Roll),"exact bot boundary");
        var urgent=T(true);int before=rolls;C(p.Evaluate(urgent,c,"a","s",15,Roll)&&rolls==before,"grenade bypasses extra random roll");p.Played(urgent,"a","s",15);
        C(!p.Evaluate(T(),c,"a","s",16,Roll),"successful urgent alert spaces ordinary follow-up");
        C(p.Evaluate(T(true),c,"a","s",16,Roll),"urgent bypasses spacing");
        c.Mode="Disabled";C(!p.Evaluate(T(true),c,"a","s",16,Roll),"disabled mutes urgent squad alerts too");
        c.Mode="Normal";C(p.Evaluate(T(),c,"a","s",16,Roll),"native mode ignores COMET spacing");
        c.Mode="Reduced";c.BotDelay=c.SquadDelay=0;
        int kept=0;for(int i=0;i<100;i++){var q=T();if(p.Evaluate(q,c,"a","s",20,()=>i+.5))kept++;}C(kept==50,"50 percent sample threshold");
        c.Chance=0;C(!p.Evaluate(T(),c,"a","s",20,()=>0),"0 percent excludes ordinary");C(p.Evaluate(T(true),c,"a","s",20,Roll),"0 percent still urgent");
        c.Chance=100;C(p.Evaluate(T(),c,"a","s",20,()=>99.999),"100 percent");
        c.BotDelay=5;c.SquadDelay=2;p.Played(T(),"a","s",20);C(p.Evaluate(T(),c,"a","s",1,Roll),"clock rollback clears state");
        p.Played(T(),"a","s",1);p.Clear();C(p.Evaluate(T(),c,"a","s",1,Roll),"new raid clears clocks");
        C(p.Evaluate(T(),c,"a","s",double.NaN,Roll),"invalid clock fails open");
        // Read the actual pinned DLL: missing/renamed producers must fail before a user starts a raid.
        using(var pe=new PEReader(File.OpenRead("references/sain-4.5.1/SAIN.dll"))){var md=pe.GetMetadataReader();
            TypeDefinition Type(string name)=>md.TypeDefinitions.Select(md.GetTypeDefinition).Single(x=>md.GetString(x.Name)==name);
            string[] Methods(string name)=>Type(name).GetMethods().Select(md.GetMethodDefinition).Select(m=>md.GetString(m.Name)).ToArray();
            foreach(var name in new[]{"Say","GroupSay","TalkAfterDelay","checkTalk","CheckPriority","tellSpeakerToSay","Dispose"})C(Methods("SAINBotTalkClass").Contains(name),"native talk hook "+name);
            C(Methods("SAINBotTalkClass").Count(x=>x=="CheckPriority")==2,"two native priority overloads");
            var fields=Type("SAINBotTalkClass").GetFields().Select(md.GetFieldDefinition).Select(f=>md.GetString(f.Name)).ToArray();
            foreach(var field in new[]{"_botTalkPackage","_talkDelayPackage","_talkCacheTimer","_talkDelayTimer"})C(fields.Contains(field),"native read-only queue field "+field);
            foreach(var name in new[]{"OnDecisionMade","OnMemberMadeDecision","TalkSelfDecision","TalkSoloDecision","LeaderMadeDecision","ShallReportLostVisual","EnemyConversation","TalkEnemySniper","onEnemyHealthChanged","CheckEnemyContact","OnEnemyDown","friendlyDown","OnLootBody","allMembersSay","UpdateLeaderCommand","myHealthChanged","ShallReportNeedHelp","enemyHeard","CheckIfLeaderShouldCommand","checkLeaderTalk","TalkEnemyLocation"})C(Methods("GroupTalk").Contains(name),"native producer "+name);
            C(Methods("ThrowGrenadeAction").Contains("Start"),"own grenade producer");C(Methods("Enemy").Contains("SetEnemyAsSniper"),"sniper producer");C(Methods("PlayerComponent").Contains("PlayVoiceLine"),"native clip result");
        }
        Console.WriteLine($"{n} S09 policy/config/native-contract checks passed");return n;
    }
}
