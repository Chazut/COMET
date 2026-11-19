using Comet.Client;
using Comet.Core;
using EFT.InventoryLogic;

int checks = 0;
void Check(bool ok, string why)
{
    checks++;
    if (!ok)
        throw new Exception(why);
}
foreach (
    var mode in new[] { "always-animate", "cull-update-transforms", "cull-completely", "unknown" }
)
{
    var p = new Plugin();
    p.Frame.BodyAnimationCulling = mode;
    p.Frame.Combat.Now = 10.1;
    p.State.CombatLean.Policy.Observe(p.Frame, new CombatLeanConfig(), 0, 0);
    Check(
        p.State.CombatLean.Policy.Trace!.BodyAnimationCulling == mode,
        "body culling mode is preserved as diagnostic context"
    );
    Check(
        !p.State.CombatLean.Policy.Trace.HeadMotionObserved,
        "animation culling mode alone is not proof of head movement"
    );
}
foreach (int loaded in new[] { 0, 1, 2 })
{
    var gun = new Weapon { ChamberAmmoCount = loaded };
    Check(
        Plugin.Predicate(gun, gun) == (loaded < 2 ? 0 : 2),
        "owned partial/empty barrel request reaches native ammo selection"
    );
    Check(
        Plugin.Predicate(gun, null) == loaded && Plugin.Predicate(gun, new Weapon()) == loaded,
        "unrelated/native/non-PMC calls unchanged"
    );
    gun.ReloadMode = Weapon.EReloadMode.Other;
    Check(Plugin.Predicate(gun, gun) == loaded, "other reload types untouched");
}
{
    var p = new Plugin();
    foreach (float tilt in new[] { -.2f, -1.316f, -2.964f, -4.1f })
    {
        p.Frame.SmoothedTilt = tilt;
        p.Apply();
        Check(
            p.Lean.Bot.Player.MovementContext.Calls.Last() == (tilt, true),
            "apply SAIN's smooth value rather than jumping to full lean"
        );
    }
    Check(
        p.State.CombatLean.Policy.Trace!.AnimatorApplied
            && !p.State.CombatLean.Policy.Trace.HeadMotionObserved,
        "animator command does not invent physical head movement"
    );
    p.End();
    Check(
        p.Lean.Bot.Player.MovementContext.Calls.Last() == (0f, true)
            && !p.State.CombatLean.Owns
            && p.State.CombatLean.Owner == null,
        "owned animation released"
    );
}
foreach (
    var exclude in new Action<Plugin>[]
    {
        p => p.Frame.Combat.Pmc = false,
        p => p.Frame.Combat.Ghost = true,
        p => p.Frame.Combat.Authority = false,
        p => p.Frame.Combat.Urgent = true,
        p => p.Frame.Combat.Action = "Search",
        p => p.Frame.SimplifiedSkeleton = true,
    }
)
{
    var p = new Plugin();
    exclude(p);
    p.Apply();
    Check(
        p.Lean.Bot.Player.MovementContext.Calls.Count == 0 && !p.State.CombatLean.Policy.Active,
        "scope/urgency/simplified skeleton cannot acquire physical animation"
    );
}
foreach (
    var otherOwner in new Action<Plugin>[]
    {
        p => p.State.CombatLean.Owns = false,
        p => p.Lean.LeanDirection = SAIN.LeanSetting.Right,
        p => p.Lean.IsHoldingLean = true,
        p => p.Lean.Bot.Cover.CoverInUse = new(),
    }
)
{
    var p = new Plugin();
    p.Frame.SmoothedTilt = -2;
    p.Apply();
    otherOwner(p);
    p.End();
    Check(
        p.Lean.Bot.Player.MovementContext.Calls.Count == 1,
        "cleanup preserves lean now owned by another action"
    );
}
{
    var p = new Plugin();
    p.Lean.Bot.Player.MovementContext.Throw = true;
    p.Apply();
    Check(
        p.State.CombatLean.Fault && !p.State.CombatLean.Owns && !p.State.CombatLean.Policy.Active,
        "native error releases ownership"
    );
}
Console.WriteLine($"PASS {checks} production M04 animation / W01 barrel predicate checks");
