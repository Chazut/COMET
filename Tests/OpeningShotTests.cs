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

        Check(OpeningShot.LowThreat(ContactRole.Scav, .1f, false), "calm broadside scav");
        Check(!OpeningShot.LowThreat(ContactRole.Scav, .8f, false), "scav facing shooter");
        Check(!OpeningShot.LowThreat(ContactRole.Scav, -1, true), "incoming fire or another contact");
        Check(OpeningShot.LowThreat(ContactRole.Unknown, -.9f, false), "ordinary opponent facing away");
        Check(!OpeningShot.LowThreat(ContactRole.Unknown, .1f, false), "sideways is not back turned");
        foreach (var role in new[] { ContactRole.Boss, ContactRole.Guard, ContactRole.Soldier })
            Check(!OpeningShot.LowThreat(role, -1, false), role + " stays a threat");

        var shot = new OpeningShot(10f, 9.8f, true);
        Check(!shot.ObserveShot(-1f), "unarmed observation");
        Check(!shot.AllowTrigger(10f, false, true), "cannot override native refusal");
        Check(!shot.AllowTrigger(10.1f, true, true), "additional time starts at native readiness");
        Check(!shot.AllowTrigger(10.4f, false, true), "interrupted aim loses the settle time");
        Check(!shot.AllowTrigger(10.5f, true, true), "resumed aim waits");
        Check(!shot.AllowTrigger(11f, true, false), "unsteady aim cannot fire");
        Check(shot.AllowTrigger(11.1f, true, true), "steady opening can fire");
        Check(!shot.ObserveShot(10.9f), "another trigger must not be released");
        Check(shot.ObserveShot(11.1f), "actual shot on the matching press");
        Check(!shot.ObserveShot(11.1f), "single release per contact");
        Check(!shot.Continue(11.2f, 9.8f, true), "no second opening on the same contact");

        var angling = new OpeningShot(0, 0, false);
        Check(!angling.Continue(.2f, 0, false), "pressure cancels without delaying native fire");
        Check(angling.AllowTrigger(.2f, true, false), "native readiness restored at cancellation");
        Check(!angling.AllowTrigger(.2f, false, true), "native refusal still wins");
        Check(!new OpeningShot(0, 0, true).Continue(.2f, .19f, true), "lost and regained sight");
        Check(!new OpeningShot(0, 0, false).Continue(1.6f, 0, true), "bounded wait for alignment");
        return checks;
    }
}
