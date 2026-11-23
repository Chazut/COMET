using Comet.Core;

internal static class OpeningShotTests
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool ok, string message)
        {
            checks++;
            if (!ok)
                throw new Exception("Opening shot: " + message);
        }

        Check(OpeningShot.LowThreat(.1f, false), "calm broadside target of any role");
        Check(OpeningShot.LowThreat(-.9f, false), "target facing away");
        Check(!OpeningShot.LowThreat(.75f, false), "target facing shooter");
        Check(!OpeningShot.LowThreat(-1f, true), "incoming fire or another contact");

        foreach (var (distance, seconds) in new[] { (2f, 1f), (10f, 1f), (55f, 1.5f), (100f, 2f), (180f, 2f) })
        {
            var opening = new OpeningShot(10f, 10f, true, distance);
            Check(!opening.AllowTrigger(10f, false, true), "native aim still acquiring");
            Check(!opening.AllowTrigger(11.25f, true, true), "wait begins when native aim is ready");
            float fireAt = 11.25f + seconds;
            Check(opening.Continue(fireAt, 10f, true), "timeout allows the full settle at " + distance);
            Check(!opening.AllowTrigger(fireAt - .01f, true, true), "cannot fire early at " + distance);
            Check(opening.AllowTrigger(fireAt, true, true), "ready at the expected distance-specific time");
        }

        var shot = new OpeningShot(10f, 9.8f, true, 10f);
        Check(!shot.ObserveShot(-1f), "unarmed observation");
        Check(!shot.AllowTrigger(10f, false, true), "cannot override native refusal");
        Check(!shot.AllowTrigger(10.1f, true, true), "additional time starts at native readiness");
        Check(!shot.AllowTrigger(10.4f, false, true), "interrupted aim loses the settle time");
        Check(!shot.AllowTrigger(10.5f, true, true), "resumed aim waits");
        Check(!shot.AllowTrigger(11.1f, true, true), "interrupted readiness must start a full new wait");
        Check(!shot.AllowTrigger(11.5f, true, false), "unsteady aim cannot fire even after the wait");
        Check(shot.AllowTrigger(11.6f, true, true), "steady opening can fire");
        Check(!shot.ObserveShot(10.9f), "another trigger must not be released");
        Check(shot.ObserveShot(11.6f), "actual shot on the matching press");
        Check(!shot.ObserveShot(11.6f), "single release per contact");
        Check(!shot.Continue(11.7f, 9.8f, true), "no second opening on the same contact");

        foreach (bool eligible in new[] { OpeningShot.LowThreat(.8f, false), OpeningShot.LowThreat(-1f, true) })
        {
            var interrupted = new OpeningShot(0, 0, false, 100f);
            Check(!interrupted.AllowTrigger(0f, true, true), "long opening in progress");
            Check(!interrupted.Continue(1.8f, 0, eligible), "turn or pressure interrupts long hold");
            Check(interrupted.AllowTrigger(1.8f, true, false), "native readiness restored immediately");
            Check(!interrupted.AllowTrigger(1.8f, false, true), "native refusal still wins");
        }
        Check(!new OpeningShot(0, 0, true, 55f).Continue(.2f, .19f, true), "lost and regained sight");
        Check(!new OpeningShot(0, 0, false, 180f).Continue(3.6f, 0, true), "bounded wait for alignment");
        return checks;
    }
}
