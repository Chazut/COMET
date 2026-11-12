using Comet.Core;
using Comet.Server;
using System.Text.Json;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

internal static class DoorReservationTests
{
    internal static int Run()
    {
        int n=0;void Check(bool value,string why){if(!value)throw new Exception("D14: "+why);n++;}
        void Invalid(Action a){try{a();}catch(ArgumentException){n++;return;}throw new Exception("D14 accepted invalid settings");}
        var c=new DoorReservationConfig();c.Validate();
        Check(c.Enabled&&c.LeaseSeconds==150&&c.RenewInterval==.5&&c.BusyRetry==2,"approved defaults");
        Check(JsonSerializer.Deserialize<CometConfig>("{}")!.DoorReservation.LeaseSeconds==150,"old config loads defaults");
        foreach(var prop in typeof(DoorReservationConfig).GetProperties().Where(p=>p.PropertyType==typeof(double)))
        foreach(var bad in new[]{-1d,0,double.NaN,double.PositiveInfinity,1000})
        {var cfg=new DoorReservationConfig();prop.SetValue(cfg,bad);Invalid(cfg.Validate);}
        Invalid(()=>new DoorReservationConfig{LeaseSeconds=1,RenewInterval=1}.Validate());
        Invalid(()=>new CometConfig{DoorReservation=null!}.Validate());
        var dir=Path.Combine(Path.GetTempPath(),"comet-d14-"+Guid.NewGuid().ToString("N"));
        try
        {
            var store=new ConfigStore(Path.Combine(dir,"config.json"));var cfg=store.Read();
            cfg.DoorReservation.Enabled=false;cfg.DoorReservation.LeaseSeconds=3;cfg.DoorReservation.BusyRetry=4;
            var saved=store.Save(cfg,cfg.Revision);Check(!store.Read().DoorReservation.Enabled&&store.Read().DoorReservation.BusyRetry==4,"server persistence");
            saved.DoorReservation.LeaseSeconds=-1;Invalid(()=>store.Save(saved,saved.Revision));Check(store.Read().DoorReservation.LeaseSeconds==3,"invalid save atomic");
        }finally{Directory.Delete(dir,true);}
        var board=new DoorReservationBoard();
        Check(board.Acquire("squad","door","a","i","approach",100,250,c,out var a,out _),"first source claim");
        Check(!board.Acquire("enemy","door","b","j","entry",100,250,c,out _,out _),"source claim is global across groups");
        Check(board.Acquire("squad","other-door","b","j","approach",100,250,c,out var b,out _),"other door remains available");
        Check(board.Owns(a!,"squad","i",149),"no added short lease expiration");
        Check(board.Renew(a!,"squad","i","entry",150,true,c)&&a!.Until==250,"phase change retains original session expiry");
        Check(!board.Renew(a!,"squad","respawn","entry",151,true,c)&&board.Find("squad","door",151)==null,"new instance cannot inherit claim");
        Check(board.Release(b!),"lifecycle release");
        for(int i=0;i<40;i++)Check(board.Acquire("s"+i,"door"+i,"a"+i,"i"+i,"approach",200,350,c,out _,out _),"independent doors");
        board.Sweep(349);Check(board.Count==40,"source 150-second claims live");board.Sweep(350);Check(board.Count==0,"source timeout cleanup");
        Check(board.Acquire("s","d","a","i","approach",400,550,c,out var old,out _),"claim before reset");
        board.Clear();Check(board.Count==0&&!board.Release(old!),"raid clear rejects stale release");
        Check(board.Acquire("s","d","a","i","approach",0,150,c,out var fresh,out _)&&fresh!.Token>old!.Token,"clock and generation reset");
        Check(!board.Release(old!)&&board.Owns(fresh!,"s","i",1),"old callback cannot delete new raid claim");
        foreach(double bad in new[]{double.NaN,double.PositiveInfinity,-1d})Check(!board.Acquire("s","d","a","i","approach",bad,210,c,out _,out _),"invalid clock");
        board.Clear();Check(board.Acquire("s","d","a","i","approach",1,150,new(){Enabled=false},out var disabled,out _)&&disabled==null&&board.Count==0,"disabled pass through");
        Console.WriteLine($"D14: {n} source reservation checks");return n;
    }
}
