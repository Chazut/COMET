internal static class ControlIntegration
{
    internal static int Checks;
    internal static bool Sleeping(bool ghost) { Checks++; return ghost; }
    internal static bool WatchControl(bool enabled) { Checks++; return enabled; }
}
