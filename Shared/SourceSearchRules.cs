namespace Comet.Core;

public static class SourceSearchRules
{
    public static bool QuietHeard(bool seen, bool heard, double age, bool searches, bool searchesAudio, double wait)
        => !seen&&heard&&age>=15&&searches&&(!searchesAudio||wait>15);
    public static bool PartialEnd(bool partial, int corners, double endDistanceSquared)
        => partial&&corners>=1&&endDistanceSquared<=4;
}
