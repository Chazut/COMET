using Comet.Preview;
using Comet.Server;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:18767");
builder.WebHost.UseStaticWebAssets();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
var store = new ConfigStore();
builder.Services.AddSingleton(store);
var app = builder.Build();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

// Isolated browser-test input only. Not part of the shipped server or enabled by default.
if (Environment.GetEnvironmentVariable("COMET_PREVIEW_INTEGRATION") == "1")
    app.MapPost("/preview/integration", (Comet.Core.IntegrationReport report) =>
        store.Integration.Receive(report.Run, report, DateTime.UtcNow)
            ? Results.Ok()
            : Results.BadRequest()
    );
app.Run();
