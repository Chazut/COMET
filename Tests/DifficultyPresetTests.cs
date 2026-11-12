using Comet.Core;
using Comet.Server;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class DifficultyPresetTests
{
    public static int Run()
    {
        int checks=0;
        void Check(bool ok,string why){checks++;if(!ok)throw new Exception("U01: "+why);}
        var hard=DifficultyPresets.Create("Hard"); var normal=DifficultyPresets.Create("Normal"); var easy=DifficultyPresets.Create("Easy");
        Check(JsonSerializer.Serialize(hard)==JsonSerializer.Serialize(new CometConfig()),"Hard exactly preserves constructor defaults");
        Check(hard.BehaviorBaseline==CometConfig.SourceBaseline,"Hard uses the pinned zzap source baseline");
        Check(hard.CornerChase.Cooldown==0&&hard.DoorStepPeek.Chance==100,"source cooldown and step preference");
        var original=DifficultyPresets.Values(hard);
        string[] families={"DoorJumpPeek","DoorFakeGrenade","DoorRoomClear","DoorAmbush","CornerChase","CornerPrefire","CornerPeek","CombatLean","CoverFeint","OutdoorRelocation","TacticalJump","GrenadeJudgment","SquadManeuver","SquadStorm","SecondaryEntrance"};
        foreach(var preset in new[]{hard,normal,easy})
        {
            preset.Validate(); var context=DifficultyPresets.Describe(preset);
            Check(context.BaseProfile==preset.DifficultyPreset&&!context.Customized&&context.ChangedSettings==0,"standard profile identified");
            var values=DifficultyPresets.Values(preset); Check(values.Count==original.Count,"no settings lost");
            foreach(var item in original)
            {
                Check(values.ContainsKey(item.Key),"setting preserved "+item.Key);
                if(Equals(values[item.Key],item.Value))continue;
                Check(families.Any(f=>item.Key.StartsWith(f+".")),"only tactical whitelist families change: "+item.Key);
                Check(item.Value is double&&values[item.Key] is double,"no feature toggles, modes or structural changes: "+item.Key);
                double value=(double)values[item.Key]!, before=(double)item.Value!;
                bool retry=item.Key.Contains("Cooldown")||item.Key.EndsWith("Retry")||item.Key.EndsWith("RetryDelay")||item.Key.EndsWith("RetryMin")||item.Key.EndsWith("RetryMax");
                Check(retry ? value>=before : value<=before,"lower profiles cannot raise tactical pressure: "+item.Key);
            }
        }
        var n=DifficultyPresets.Values(normal);var e=DifficultyPresets.Values(easy);
        foreach(var item in original.Where(x=>x.Value is double))
        {
            double h=(double)item.Value!,nv=(double)n[item.Key]!,ev=(double)e[item.Key]!;
            Check((h>=nv&&nv>=ev)||(h<=nv&&nv<=ev),"ordered levels: "+item.Key);
            if(h==0)Check(ev==0&&nv==0,"zero chance/disabled parameter never activated");
        }
        Check(easy.SquadManeuver.Chance==30&&normal.SquadManeuver.Chance==45&&hard.SquadManeuver.Chance==60,"squad examples");
        Check(easy.CoverFeint.PeekChance==17.5&&normal.CoverFeint.PeekChance==26.25&&hard.CoverFeint.PeekChance==35,"feint examples");
        Check(easy.CoverFeint.RetryDelay==30&&normal.CoverFeint.RetryDelay==25&&hard.CoverFeint.RetryDelay==20,"delay examples");
        Check(normal.DoorFakeGrenade.Chances["GigaChad"]>normal.DoorFakeGrenade.Chances["Normal"]&&normal.DoorFakeGrenade.Chances["Timmy"]==0,"personality ordering and exclusions kept");
        Check(normal.DoorRunBy.Preference==hard.DoorRunBy.Preference&&normal.DoorFakeMedical.Preference==hard.DoorFakeMedical.Preference,"shared roll not scaled twice via style mix");
        Check(!easy.DoorClosing.Enabled&&!normal.DoorClosing.Enabled,"optional door closing remains off");
        var savedHard=JsonSerializer.Serialize(hard);
        normal.DoorFakeGrenade.Chances["Chad"]=0;normal.GrenadeJudgment.BaseChance=1;
        Check(JsonSerializer.Serialize(hard)==savedHard,"profiles share no dictionaries");
        Check(DifficultyPresets.Describe(normal).ChangedSettings==2,"manual changes detected across dictionary and scalar");
        normal=DifficultyPresets.Create("Normal");normal.Revision=40;
        Check(!DifficultyPresets.Describe(normal).Customized,"server revisions are not custom settings");
        normal.Enabled=false;Check(DifficultyPresets.Describe(normal).Customized,"feature switches count as customization");
        for(int i=0;i<15;i++){var fresh=DifficultyPresets.Create(i%2==0?"Easy":"Normal");Check(fresh.SquadManeuver.Chance==(i%2==0?30:45),"switching does not compound multipliers");}
        try{DifficultyPresets.Create("Impossible");Check(false,"unknown preset must reject");}catch(ArgumentException){checks++;}
        try{new CometConfig{DifficultyPreset=""}.Validate();Check(false,"invalid saved profile must reject");}catch(ArgumentException){checks++;}

        string dir=Path.Combine(Path.GetTempPath(),"comet-u01-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        try
        {
            string path=Path.Combine(dir,"config.json");var store=new ConfigStore(path);
            Check(store.Read().DifficultyPreset=="Hard","fresh installation defaults to highest profile");
            var oldDefaults=JsonSerializer.SerializeToNode(new CometConfig())!.AsObject();
            oldDefaults.Remove("BehaviorBaseline");oldDefaults["CornerPeek"]!["Chance"]=13;
            string legacy=oldDefaults.ToJsonString();File.WriteAllText(path,legacy);
            var migrated=store.Read();Check(migrated.DifficultyPreset=="Hard"&&!DifficultyPresets.Describe(migrated).Customized,"legacy default loads as Hard");
            Check(Directory.GetFiles(dir,"*.before-zzap-*.bak").Any(p=>File.ReadAllText(p)==legacy),"migration preserves exact previous settings in backup");
            var v1=JsonNode.Parse(legacy)!.AsObject();v1["BehaviorBaseline"]="zzap-fbb86b83-1";
            v1["DoorSearchClear"]!["ContactLifetime"]=25;v1["CloseDetection"]!["ReductionPercent"]=50;
            v1["GasExposure"]!["Enabled"]=false;v1["ImpactReaction"]!["Enabled"]=false;
            string v1Path=Path.Combine(dir,"v1.json"),v1Bytes=v1.ToJsonString();File.WriteAllText(v1Path,v1Bytes);
            var v1Store=new ConfigStore(v1Path);var v2=v1Store.Read();
            Check(v2.BehaviorBaseline==CometConfig.SourceBaseline&&v2.DoorSearchClear.ContactLifetime==120&&v2.CloseDetection.ReductionPercent==100,"follow-up migration replaces v1 perception limits");
            Check(!v2.GasExposure.Enabled&&!v2.ImpactReaction.Enabled,"follow-up migration keeps deliberate feature disables");
            Check(Directory.GetFiles(dir,"*.before-zzap-*.bak").Any(p=>File.ReadAllText(p)==v1Bytes),"follow-up migration backs up exact v1 bytes");
            int backups=Directory.GetFiles(dir,"*.before-zzap-*.bak").Length;v1Store.Read();
            Check(Directory.GetFiles(dir,"*.before-zzap-*.bak").Length==backups,"follow-up migration runs once");
            foreach(var retired in new[]{"ImpactReaction.Memory","ImpactReaction.MinimumInterval","ExplosionAwareness.LookDuration","ExplosionAwareness.LookInterval","GasExposure.AttemptDuration","GasExposure.RetryDelay","Gunfire.LookDuration","Gunfire.NearDistance"})
                Check(SourceBaselineSettings.IsRetiredPath(retired),"obsolete behavior control hidden: "+retired);
            Check(!SourceBaselineSettings.IsRetiredPath("ExplosionAwareness.Enabled"),"G10 enable toggle is functional");
            var legacyCustom=JsonNode.Parse(legacy)!.AsObject();legacyCustom["CornerPeek"]!["Chance"]=13;
            File.WriteAllText(path,legacyCustom.ToJsonString());migrated=store.Read();
            Check(migrated.CornerPeek.Chance==hard.CornerPeek.Chance&&!DifficultyPresets.Describe(migrated).Customized,"old behavioral limits replaced by source defaults");
            var raid=store.Read();var editor=DifficultyPresets.Create("Easy");var saved=store.Save(editor,raid.Revision);
            Check(saved.DifficultyPreset=="Easy"&&saved.Revision==raid.Revision+1,"profile saved with normal revision protocol");
            Check(raid.CornerPeek.Chance==hard.CornerPeek.Chance&&raid.DifficultyPreset=="Hard","saving does not change running raid snapshot");
            editor.CornerPeek.Chance=99;Check(store.Read().CornerPeek.Chance==25,"server saved detached values");
            Check(File.Exists(path+".bak"),"existing server backup preserved");
            var network=Newtonsoft.Json.JsonConvert.DeserializeObject<CometConfig>(store.ForGame())!;network.Validate();
            Check(!DifficultyPresets.Describe(network).Customized&&network.DifficultyPreset=="Easy","server-to-client JSON matches selected profile");
            try{store.Save(hard,raid.Revision);Check(false,"stale editor must reject");}catch(InvalidOperationException){checks++;}
        }
        finally {Directory.Delete(dir,true);}
        Console.WriteLine($"U01 difficulty profiles: {checks} checks.");return checks;
    }
}
