using System.Numerics;

int n = 0;
void Check(bool ok, string why)
{
    n++;
    if (!ok)
        throw new Exception(why);
}
var equipment = new InventoryEquipment();
var bag = new Item();
equipment.Slots[EquipmentSlot.Backpack] = new() { ContainedItem = bag };
Check(
    Runtime.RootSlot(new Item(), equipment) == null,
    "depleted detached medical item does not call throwing Parent"
);
var med = new Item { CurrentAddress = new() { Container = new() { ParentItem = bag } } };
Check(
    Runtime.RootSlot(med, equipment) == EquipmentSlot.Backpack,
    "nested carried medical item remains available"
);
var belt = new Item();
equipment.Slots[EquipmentSlot.ArmBand] = new() { ContainedItem = belt };
var magazine = new Item { CurrentAddress = new() { Container = new() { ParentItem = belt } } };
Check(
    Runtime.RootSlot(magazine, equipment) == EquipmentSlot.ArmBand,
    "worn belt resource reports actual slot through production ancestry reader"
);
magazine.CurrentAddress = null;
Check(Runtime.RootSlot(magazine, equipment) == null, "detached belt resource is no longer carried");
med.CurrentAddress!.Container!.ParentItem = med;
Check(Runtime.RootSlot(med, equipment) == null, "self-referencing inventory cannot loop");
var other = new Item { CurrentAddress = new() { Container = new() { ParentItem = med } } };
med.CurrentAddress.Container.ParentItem = other;
Check(
    Runtime.RootSlot(med, equipment) == null,
    "multi-item inventory cycle bounded to sixteen parents"
);
{
    var p = new Player();
    p.Inventory.Equipment = equipment;
    var item = new Item { Med = new() };
    var treatment = new BotAbstractMedsToPart { CurUsingMeds = item };
    Check(
        !Runtime.Medical(treatment, p) && p.HealthController.Calls == 0,
        "detached treatment item never enters native CanApplyItem"
    );
    item.CurrentAddress = new() { Container = new() { ParentItem = bag } };
    item.Med.HpResource = 0;
    Check(
        !Runtime.Medical(treatment, p) && p.HealthController.Calls == 0,
        "empty treatment item never enters native CanApplyItem"
    );
    item.Med.HpResource = 100;
    Check(
        Runtime.Medical(treatment, p) && p.HealthController.Calls == 1,
        "valid carried supply still checks native healing eligibility"
    );
    p.HealthController.Allowed = false;
    Check(!Runtime.Medical(treatment, p), "native treatment refusal preserved");
}
foreach (
    var outcome in new[]
    {
        "normal",
        "retarget",
        "replace",
        "heal",
        "grenade",
        "reload",
        "throw",
        "self",
        "squad",
        "air",
        "retreat",
    }
)
{
    var b = new BotComponent();
    var first = b.Mover.One;
    var queued = b.Mover.Two;
    first.Status = EBotMoveStatus.Moving;
    queued.Status = EBotMoveStatus.ReadyToMove;
    queued.Destination = new(3, 0, 0);
    b.Mover.ActivePath = first;
    first.OnStop = () =>
    {
        b.Mover.ActivePath = queued;
        queued.Status = EBotMoveStatus.Moving;
        queued.TimeStarted = Time.time;
        switch (outcome)
        {
            case "retarget":
                queued.Destination = new(9, 0, 0);
                break;
            case "replace":
                b.Mover.ActivePath = new OtherPath();
                break;
            case "heal":
                b.BotOwner.Medecine.Using = true;
                break;
            case "grenade":
                b.Grenade.GrenadeDangerPoint = new();
                break;
            case "reload":
                b.BotOwner.WeaponManager.Reload.Reloading = true;
                break;
            case "throw":
                b.BotOwner.WeaponManager.Grenades.ThrowindNow = true;
                break;
            case "self":
                b.Decision.CurrentSelfDecision = ESelfActionType.FirstAid;
                break;
            case "squad":
                b.Decision.CurrentSquadDecision = ESquadDecision.Regroup;
                break;
            case "air":
                b.Player.MovementContext.IsGrounded = false;
                break;
            case "retreat":
                b.Decision.CurrentCombatDecision = ECombatDecision.Retreat;
                break;
        }
    };
    queued.OnStop = () => b.Mover.ActivePath = null;
    bool accepted = Runtime.Release(b, out int released);
    Check(accepted == (outcome == "normal"), "shooting handoff result: " + outcome);
    Check(
        released == (outcome == "normal" ? 2 : 1) && queued.Stops == (outcome == "normal" ? 1 : 0),
        "only exact still-safe queued shooting route released: " + outcome
    );
}
foreach (
    var kind in new[]
    {
        "heal",
        "grenade",
        "reload",
        "throw",
        "self",
        "squad",
        "air",
        "retreat",
        "other",
    }
)
{
    var b = new BotComponent();
    b.Mover.ActivePath = b.Mover.One;
    b.Mover.One.Status = EBotMoveStatus.Moving;
    switch (kind)
    {
        case "heal":
            b.BotOwner.Medecine.Using = true;
            break;
        case "grenade":
            b.Grenade.GrenadeDangerPoint = new();
            break;
        case "reload":
            b.BotOwner.WeaponManager.Reload.Reloading = true;
            break;
        case "throw":
            b.BotOwner.WeaponManager.Grenades.ThrowindNow = true;
            break;
        case "self":
            b.Decision.CurrentSelfDecision = ESelfActionType.FirstAid;
            break;
        case "squad":
            b.Decision.CurrentSquadDecision = ESquadDecision.Regroup;
            break;
        case "air":
            b.Player.MovementContext.IsGrounded = false;
            break;
        case "retreat":
            b.Decision.CurrentCombatDecision = ECombatDecision.Retreat;
            break;
        case "other":
            b.Mover.ActivePath = new OtherPath();
            break;
    }
    Check(
        !Runtime.Release(b, out var count) && count == 0 && b.Mover.One.Stops == 0,
        "initial native priority preserved: " + kind
    );
}
foreach (bool afterSecond in new[] { false, true })
{
    var b = new BotComponent();
    var first = b.Mover.One;
    var second = b.Mover.Two;
    first.Status = EBotMoveStatus.Moving;
    b.Mover.ActivePath = first;
    if (afterSecond)
        second.Status = EBotMoveStatus.ReadyToMove;
    first.OnStop = () =>
    {
        if (afterSecond)
        {
            b.Mover.ActivePath = second;
            second.Status = EBotMoveStatus.Moving;
            second.TimeStarted = Time.time;
        }
        else
        {
            b.Mover.ActivePath = null;
            b.BotOwner.Medecine.Using = true;
        }
    };
    second.OnStop = () =>
    {
        b.Mover.ActivePath = null;
        b.Grenade.GrenadeDangerPoint = new();
    };
    Check(
        !Runtime.Release(b, out var released) && released == (afterSecond ? 2 : 1),
        "urgent state created by native completion still cancels handoff, with no remaining path"
    );
}
Console.WriteLine($"PASS {n} production inventory and shooting-route regressions");
