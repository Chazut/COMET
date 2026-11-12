using Comet.Core;
static class SearchAbandonmentTests
{
    public static int Run(){int n=0;void Check(bool x,string s){n++;if(!x)throw new Exception(s);}
        var c=new SearchAbandonmentConfig();var p=new SearchAbandonment();var f=new SearchAbandonmentFrame{Target="enemy",Now=20,Stamp=0,PersonalSearch=true};
        Check(p.Evaluate(f,c).ForgetRequested,"source reached and silent 20 seconds");
        f.Visible=true;Check(!p.Evaluate(f,c).ForgetRequested,"visible kept");f.Visible=false;
        f.Now=19.99;Check(!p.Evaluate(f,c).ForgetRequested,"source silence threshold");f.Now=20;
        f.PersonalSearch=false;Check(!p.Evaluate(f,c).ForgetRequested,"unvisited place kept");f.PersonalSearch=true;
        f.Distance=100;f.Unsafe=true;f.Active=false;f.Sampled=f.Facing=f.ClearView=f.SameFloor=false;
        Check(p.Evaluate(f,c).ForgetRequested,"no COMET geometry inspection or priority veto");
        Check(p.ObserveForgotten(f,c,true)&&p.RememberedCount==0,"no artificial failed-memory ban");
        f.Stamp=1;Check(!p.Evaluate(f,c).ForgetRequested,"new evidence resets source age");return n;
    }
}
