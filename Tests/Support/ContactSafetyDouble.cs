namespace Comet.Client;
// These isolated action harnesses do not model SAIN/native squad ownership.
// The production predicate is exercised separately in ContactSafetyHarness.
internal static class ContactSafety
{
    internal static bool Allowed=true;
    internal static bool Allows(object bot,object? enemy)=>Allowed;
    internal static bool AllowsGoal(object bot)=>Allowed;
}
