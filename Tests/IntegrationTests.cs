using Comet.Core;
using Comet.Server;

internal static class IntegrationTests
{
    public static int Run()
    {
        int checks=0;
        void Check(bool ok,string why){checks++;if(!ok)throw new Exception("I01: "+why);}
        void Throws(Action action,string why){try{action();}catch{checks++;return;}throw new Exception("I01 expected refusal: "+why);}
        var boot=new IntegrationBoot();
        Check(!boot.Ready,"unverified cannot act");
        boot.Step("official",()=>Check(!boot.Ready,"not active during verification"));
        boot.Step("hooks",()=>Check(!boot.Ready,"not active during partial installation"));
        boot.Complete(5,9);Check(boot.Ready,"commit verified startup");
        Throws(()=>boot.Step("late",()=>{}),"late registration");
        Throws(()=>new IntegrationBoot().Complete(0,0),"empty installation");
        var failed=new IntegrationBoot();int rollbacks=0;
        try {failed.Step("missing-hook",()=>throw new MissingMethodException("GetDecision"));}
        catch(Exception ex){failed.Fail(ex,()=>rollbacks++);}
        Check(!failed.Ready&&failed.Report.Phase=="missing-hook"&&rollbacks==1,"failure keeps stage and rolls back");
        Check(failed.Report.Outcome=="blocked"&&failed.Report.Message.Contains("GetDecision"),"actionable failure");
        failed.Fail(new Exception("problem"),()=>throw new Exception("rollback problem"));
        Check(!failed.Ready&&failed.Report.Message.Contains("rollback failed"),"rollback failure cannot activate");
        failed.Fail(new Exception("IL Compile Error",new InvalidOperationException("W04 interface call missing")),()=>rollbacks++);
        Check(!failed.Ready&&failed.Report.Message.Contains("IL Compile Error")&&failed.Report.Message.Contains("W04 interface call missing"),"nested patch cause reaches server UI");
        failed.Fail(new Exception("outer",new Exception(new string('x',2000))),()=>{});
        Check(failed.Report.Message.Length==1024,"nested diagnostics remain bounded");
        var hooks=new[]{("method1","prefix","Choose"),("method2","prefix","Choose"),("method1","postfix","Accepted")};
        IntegrationContract.ValidateHooks(hooks,new[]{"prefix:Choose","postfix:Accepted"});checks++;
        Throws(()=>IntegrationContract.ValidateHooks(hooks,new[]{"prefix:Missing"}),"missing hook");
        Throws(()=>IntegrationContract.ValidateHooks(hooks.Append(hooks[0]),new[]{"prefix:Choose"}),"duplicate registration");
        Throws(()=>IntegrationContract.ValidateHooks(hooks,new[]{"finalizer:Choose"}),"wrong patch kind");
        var stateHooks=new[]{("shoot","Geometry","prefix","Begin","BotState",true),("shoot","Geometry","postfix","End","BotState",false),
            ("shoot","Discipline","prefix","Begin","OtherState",true),("hear","Geometry","prefix","AnotherBegin","OtherState",true)};
        IntegrationContract.ValidateStateSlots(stateHooks);checks++;
        Throws(()=>IntegrationContract.ValidateStateSlots(stateHooks.Append(("shoot","Geometry","finalizer","Finally","OtherState",false))),"different state types share a Harmony slot");
        Throws(()=>IntegrationContract.ValidateStateSlots(stateHooks.Append(("shoot","Geometry","prefix","SecondBegin","BotState",true))),"independent state writers share a Harmony slot");
        IntegrationContract.ValidateStateSlots(stateHooks.Append(("shoot","Geometry","finalizer","Finally","BotState",false)));checks++;
        for(int flags=0;flags<32;flags++)for(int age=0;age<3;age++)
        {
            bool eligible=(flags&1)!=0,request=(flags&2)!=0,match=(flags&4)!=0,self=(flags&8)!=0,squad=(flags&16)!=0;
            bool accepted=IntegrationContract.Accept(eligible,request,100-age,100,3,match?3:4,self,squad);
            Check(accepted==(eligible&&request&&match&&!self&&!squad&&age==0),"acceptance matrix "+flags+"/"+age);
        }
        // Reused native action in the same frame is not a second acceptance after consumption.
        Check(!IntegrationContract.Accept(true,true,-1,100,3,3,false,false),"consumed request");
        var reports=new IntegrationReports();var now=new DateTime(2026,11,4,12,0,0,DateTimeKind.Utc);
        Check(reports.Read(now).Count==0,"no report is not ready");
        Check(!reports.Receive("",boot.Report,now),"empty session");
        Check(reports.Receive("session1",boot.Report,now),"valid report");
        Check(reports.Receive("session1",failed.Report,now.AddSeconds(1)),"replace session report");
        Check(reports.Read(now).Count==1&&reports.Read(now)[0].Report.Outcome=="blocked","last report wins, isolated from config");
        failed.Report.Outcome="ready";
        Check(reports.Read(now)[0].Report.Outcome=="blocked","input cloned");
        var read=reports.Read(now);read[0].Report.Message="mutated";
        Check(reports.Read(now)[0].Report.Message!="mutated","output cloned");
        for(int i=0;i<40;i++)Check(reports.Receive("session"+i,boot.Report,now.AddSeconds(i)),"bounded clients");
        Check(reports.Read(now.AddMinutes(1)).Count==32,"capacity");
        Check(reports.Read(now.AddDays(2)).Count==0,"old reports expire");
        boot.Report.Notices=new[]{new string('x',257)};Check(!reports.Receive("s",boot.Report,now),"oversize notice");
        boot.Report.Notices=Array.Empty<string>();boot.Report.Targets=9999;Check(!reports.Receive("s",boot.Report,now),"invalid count");
        Console.WriteLine($"I01: {checks} startup, hook, decision and report checks.");return checks;
    }
}
