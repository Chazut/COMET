using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Web;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Utils;
using Comet.Core;
using SPTarkov.Server.Core.Models.Utils;

namespace Comet.Server;

public record Metadata : IModMetadata, IModBlazorMetadata
{
    public string ModGuid { get; init; } = "com.chazut.comet.server";
    public string Name { get; init; } = "COMET";
    public string Author { get; init; } = "Chazut";
    public List<string>? Contributors { get; init; } = ["R_F"];
    public SemanticVersioning.Version Version { get; init; } = new(CometVersion.Release);
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.0");
    public bool HasPrepatcher { get; init; }
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public string? Url { get; init; } = "https://github.com/Chazut/COMET";
    public string License { get; init; } = "MIT";
    public string? WWWRootUrl { get; init; }
    public string? HomePage { get; init; } = "/comet";
    public string? HomePageDescription { get; init; } = "Configure COMET";
}

[Injectable]
public sealed class ConfigRouter(JsonUtil json, ConfigStore store) : StaticRouter(json, [
    new RouteAction<EmptyRequestData>("/comet/config", (url, request, session, output, ct) => new ValueTask<string>(store.ForGame())),
    new RouteAction<IntegrationReportRequest>("/comet/integration", (url, request, session, output, ct) =>
        new ValueTask<string>(store.Integration.Receive(session.ToString(), request, DateTime.UtcNow)?"{\"ok\":true}":"{\"ok\":false}"))
]) { }

public sealed class IntegrationReportRequest : IntegrationReport, IRequestData { }
