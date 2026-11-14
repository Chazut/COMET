using Comet.Core;

static class MagazineRecoveryTests
{
    public static int Run()
    {
        int checks=0;
        void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
        var queue=new MagazineRecovery<object>();
        object original=new(),second=new();
        Check(queue.Remember("original",original),"retain an actual dropped object");
        Check(!queue.Remember("original",second),"duplicate notification must not replace the original");
        queue.Remember("second",second);
        Check(ReferenceEquals(queue.Item,original)&&queue.Count==2,"preserve drop order and identity");
        queue.Finish();Check(ReferenceEquals(queue.Item,second),"next drop after completed/unavailable pickup");
        queue.Clear();Check(queue.Count==0&&queue.Item==null,"raid cleanup releases world objects");
        Check(!MagazineRecovery<object>.ShouldPause("contact-ended"),"losing a contact must allow post-combat recovery");
        Check(MagazineRecovery<object>.ShouldPause("ghost")&&!MagazineRecovery<object>.ShouldClear("ghost"),"ghost pauses without losing belongings");
        Check(MagazineRecovery<object>.ShouldClear("raid-ended"),"raid end forgets belongings");
        for(int mask=0;mask<64;mask++)
        {
            bool enabled=(mask&1)!=0,alive=(mask&2)!=0,authority=(mask&4)!=0,ghost=(mask&8)!=0,combat=(mask&16)!=0,priority=(mask&32)!=0;
            Check(MagazineRecovery<object>.CanRecover(enabled,alive,authority,ghost,combat,priority)==(mask==7),"recover only out of combat with authority: "+mask);
        }
        Check(new QuickReloadConfig().RecoverDroppedMagazine,"recovery defaults on");
        Check(Comet.Server.Editing.EditorCatalog.Read().Fields.ContainsKey("QuickReload.RecoverDroppedMagazine"),
            "standard server editor exposes recovery");
        Check(System.Text.Json.JsonSerializer.Deserialize<QuickReloadConfig>("{}")!.RecoverDroppedMagazine,
            "older saved settings enable recovery without a migration");
        Console.WriteLine($"Magazine recovery policy: {checks} checks passed.");return checks;
    }
}
