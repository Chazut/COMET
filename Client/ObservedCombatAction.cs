namespace Comet.Client;

public sealed partial class Plugin
{
    private static string ExecutionAction(BotState s) =>
        s.Feature == "A03" && s.Decision.Length > 0 ? s.SightEvent?.Action ?? "" : s.Decision;
}
