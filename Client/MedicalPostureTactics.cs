using SAIN.Components;
namespace Comet.Client;
public sealed partial class Plugin
{
    private void SuspendMedicalTactics(BotComponent b,BotState s)
    {
        const string why="medical-posture-protected";
        EndCombatLean(s,why);EndAngle(s,why);EndHoldAngle(s,why);EndWeave(s,why);
        // End only COMET-owned tactical paths on the ground. Never stop the mover,
        // the native cover/escape path, a medicine operation, or a physical jump.
        if(!b.Player.MovementContext.IsGrounded)return;
        EndCombatStep(s,why);EndCornerPeek(s,why);EndChase(s,why);
        EndDoorStep(s,why);EndDoorRun(s,why);EndDoorJump(s,why);EndFeint(s,why);
    }
}
