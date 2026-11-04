using Comet.Core;
using Comet.Server;
using System.Text.Json;

internal static class DoorSupportTests
{
    internal static int Run()
    {
        int n=0;void Check(bool ok,string why){if(!ok)throw new Exception("D11: "+why);n++;}
        void Invalid(Action a){try{a();}catch(ArgumentException){n++;return;}throw new Exception("D11 invalid configuration accepted");}
        var c=new DoorSupportConfig();c.Validate();
        Check(c.Enabled&&c.MaxDistance==15&&c.MaxSupports==2&&c.OfferLifetime==3&&c.Duration==20&&c.Spacing==1.5,"approved defaults");
        Check(JsonSerializer.Deserialize<CometConfig>("{}")!.DoorSupport.Duration==20,"old configuration migrates");
        foreach(double bad in new[]{-1d,double.NaN,double.PositiveInfinity})
        {Invalid(()=>new DoorSupportConfig{MaxDistance=bad}.Validate());Invalid(()=>new DoorSupportConfig{Spacing=bad}.Validate());Invalid(()=>new DoorSupportConfig{Duration=bad}.Validate());Invalid(()=>new DoorSupportConfig{OfferLifetime=bad}.Validate());}
        Invalid(()=>new DoorSupportConfig{MaxSupports=0}.Validate());Invalid(()=>new DoorSupportConfig{MaxSupports=3}.Validate());
        Invalid(()=>new DoorSupportConfig{Duration=31}.Validate());Invalid(()=>new DoorSupportConfig{OfferLifetime=6}.Validate());
        Invalid(()=>new DoorSupportConfig{MaxDistance=26}.Validate());Invalid(()=>new DoorSupportConfig{Spacing=4}.Validate());
        var dir=Path.Combine(Path.GetTempPath(),"comet-d11-"+Guid.NewGuid().ToString("N"));
        try{var store=new ConfigStore(Path.Combine(dir,"config.json"));var d=store.Read();d.DoorSupport.MaxSupports=1;d.DoorSupport.Duration=12;
            var saved=store.Save(d,d.Revision);Check(store.Read().DoorSupport.MaxSupports==1&&store.Read().DoorSupport.Duration==12,"server save/reload");
            saved.DoorSupport.Spacing=double.NaN;Invalid(()=>store.Save(saved,saved.Revision));Check(store.Read().DoorSupport.Spacing==1.5,"invalid save leaves saved config intact");}
        finally{Directory.Delete(dir,true);}
        DoorSupportBoard Board(double available=20,DoorSupportConfig? cfg=null){var b=new DoorSupportBoard();Check(b.Begin("op","lead","door","target",100,available,cfg??c),"operation starts");return b;}
        DoorSupportTrace? Offer(DoorSupportBoard b,string id="one",string role="overwatch",double x=3,double now=100,double length=0)
            =>b.Offer("op",id,role,new(x,0,-4),new(0,1.3,0),length,1,now,c);
        var board=Board();var first=Offer(board)!;
        Check(!first.Accepted&&!first.Arrived&&!first.AngleVerified&&first.State=="proposed","proposal is not acceptance");
        Check(!board.Begin("op","lead","door","target",101,100,c),"cannot refresh operation deadline");
        Check(Offer(board,"two")==null,"one role reservation");
        Check(Offer(board,"one","rear-guard",6)==null,"one role per recipient");
        Check(Offer(board,"two","rear-guard",4)==null,"simultaneous nearby reservation refused");
        Check(Offer(board,"two","rear-guard",4.5)!=null,"exact spacing accepted");
        Check(!board.Arrive("one",new(3,0,-4),true,true,true),"cannot report arrival before native acceptance");
        Check(board.Accept("one",102.9),"native acceptance before offer timeout");
        Check(board.Arrive("one",new(3,0,-4),true,true,true)&&first.Retained&&!first.Arrived,"valid current position retained without fabricated travel");
        Check(!board.Arrive("one",new(3,0,-4),true,false,true)&&!first.AngleVerified,"eyes must actually clear");
        board.End("one","support-visible-threat");Check(first.State=="released"&&!first.AngleVerified&&board.Reservations==1,"urgent release clears reservation");
        var replacement=Offer(board,"three",now:104)!;Check(replacement.Remaining==16,"replacement inherits original deadline");
        Check(board.Accept("three",104),"replacement accepted");board.RequestMove("three",true);
        Check(!board.Arrive("three",new(3,0,-4),false,true,true),"nearby alone is not completed path");
        Check(!board.Arrive("three",new(5,0,-4),true,true,true),"path completion alone is not arrival");
        Check(board.Arrive("three",new(3,0,-4),true,true,true)&&replacement.Arrived&&!replacement.Retained,"actual complete arrival");
        board.End("three","support-leader-ended");Check(!board.CanOffer("op","overwatch","four",105),"one replacement maximum");
        board.EndOperation("op","support-leader-ended");Check(board.Reservations==0&&!board.CanOffer("op","rear-guard","five",105),"leader disappears releases all roles");
        var expired=Board();var exp=Offer(expired)!;Check(!expired.Accept("one",103)&&exp.Reason=="support-offer-expired"&&expired.Reservations==0,"offer deadline exact boundary");
        var shortBoard=Board(2);var shortTrace=Offer(shortBoard)!;Check(shortTrace.Remaining==2&&shortTrace.OfferRemaining==2,"leader budget can shorten offer");
        Check(!shortBoard.Observe("one",102,100)&&shortBoard.Reservations==0,"fresh evidence never extends operation");
        var single=Board(cfg:new(){MaxSupports=1});Check(!single.CanOffer("op","rear-guard","one",100),"one supporter means overwatch only");
        Check(!single.CanOffer("op","overwatch","lead",100),"cannot support self");
        var fail=Board();var failTrace=Offer(fail)!;fail.Accept("one",100);fail.RequestMove("one",false);
        Check(fail.Reservations==0&&failTrace.State=="released"&&!failTrace.MovementAccepted,"move refusal frees role");
        foreach(double bad in new[]{double.NaN,double.PositiveInfinity,double.NegativeInfinity})
        {
            Check(!new DoorSupportBoard().Begin("op","lead","door","target",bad,20,c),"nonfinite time");
            Check(!new DoorSupportBoard().Begin("op","lead","door","target",100,bad,c),"nonfinite budget");
            var b=Board();Check(Offer(b,x:bad)==null&&Offer(b,length:bad)==null,"bad candidate cannot reserve");
            Offer(b);Check(!b.Observe("one",bad,20)&&b.Reservations==0,"bad observation ends lease");
        }
        for(int available=1;available<=30;available++)for(int accept=0;accept<=5;accept++)
        {
            var b=Board(available);var t=Offer(b)!;bool ok=b.Accept("one",100+accept);
            Check(ok==(accept<Math.Min(3,available)),"acceptance window bounded");
            if(ok){b.Observe("one",100+accept,1000);Check(t.Remaining<=Math.Min(20,available)-accept,"heartbeat cannot extend deadline");}
            b.Clear();Check(b.Reservations==0,"raid reset clears leases");
        }
        var fixtureBoard=Board();var fixture=Offer(fixtureBoard)!;fixtureBoard.Accept("one",100.2);fixtureBoard.Arrive("one",new(3,0,-4),true,true,true);
        var artifact=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../artifacts/d11-fixture.json"));Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        File.WriteAllText(artifact,JsonSerializer.Serialize(new{schema=1,session="d11-fixture",sequence=1,feature="D11",stage="evaluated",decision="DoorSupport",reason=fixture.Reason,
            profileId="one",name="Support PMC",botInstance="d11-support",realtime=27,doorSupport=fixture},new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase,WriteIndented=true}));
        Console.WriteLine($"D11 door support: {n} checks passed.");return n;
    }
}
