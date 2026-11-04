using Comet.Core;

namespace Comet.Server;

public sealed record ReceivedIntegrationReport(IntegrationReport Report, DateTime ReceivedUtc);

// One bounded, last-received report per authenticated SPT session. Never persists profile IDs.
public sealed class IntegrationReports
{
    private readonly object gate=new();
    private readonly Dictionary<string,ReceivedIntegrationReport> reports=new(StringComparer.Ordinal);
    public bool Receive(string session, IntegrationReport? input, DateTime now)
    {
        if(string.IsNullOrWhiteSpace(session)||session.Length>128||input==null||input.Schema!=1
            ||input.Outcome is not ("ready" or "blocked")||!Guid.TryParseExact(input.Run,"N",out _)
            ||input.Phase==null||input.Phase.Length>100||input.Message==null||input.Message.Length>1024
            ||input.Role is not ("unknown" or "solo" or "Fika (authority checked in raid)")
            ||input.Version==null||input.Version.Length>32||input.Targets<0||input.Targets>2048||input.Callbacks<0||input.Callbacks>4096
            ||input.Notices==null||input.Notices.Length>8||input.Notices.Any(s=>s==null||s.Length>256))return false;
        var copy=new IntegrationReport { Run=input.Run,Outcome=input.Outcome,Phase=input.Phase,Message=input.Message,
            Role=input.Role,Version=input.Version,Targets=input.Targets,Callbacks=input.Callbacks,Notices=input.Notices.ToArray() };
        lock(gate)
        {
            Expire(now);
            if(!reports.ContainsKey(session)&&reports.Count>=32)reports.Remove(reports.MinBy(x=>x.Value.ReceivedUtc).Key);
            reports[session]=new(copy,now);return true;
        }
    }
    public IReadOnlyList<ReceivedIntegrationReport> Read(DateTime now)
    {
        lock(gate)
        {
            Expire(now);
            return reports.Values.OrderByDescending(v=>v.ReceivedUtc).Select(v=>new ReceivedIntegrationReport(
                new IntegrationReport{Run=v.Report.Run,Outcome=v.Report.Outcome,Phase=v.Report.Phase,Message=v.Report.Message,
                    Role=v.Report.Role,Version=v.Report.Version,Targets=v.Report.Targets,Callbacks=v.Report.Callbacks,Notices=v.Report.Notices.ToArray()},v.ReceivedUtc)).ToArray();
        }
    }
    private void Expire(DateTime now)
    {foreach(var key in reports.Where(v=>now-v.Value.ReceivedUtc>TimeSpan.FromHours(24)).Select(v=>v.Key).ToArray())reports.Remove(key);}
}
