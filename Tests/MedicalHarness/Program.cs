using Comet.Client;
using Comet.Core;
using EFT;
using EFT.Ballistics;
using EFT.HealthSystem;
using EFT.InventoryLogic;
using Newtonsoft.Json.Linq;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;

int checks = 0;
var events = new List<string>();
Console.WriteLine(
    $"Medical inventory work: {Plugin.SuppliesRegression()} checks passed (7 health reads regardless of kit count)."
);
void C(bool x, string why)
{
    checks++;
    if (!x)
        throw new Exception("H01 adapter: " + why);
}
(Plugin p, BotComponent b) Setup()
{
    Time.time = 10;
    Time.frameCount++;
    var p = new Plugin();
    var b = new BotComponent();
    b.EnemyController.KnownEnemies.Add(new(b));
    return (p, b);
}
Meds Aid(BotComponent b, string id = "aid")
{
    var item = new MedKit { Id = id, AllowedPart = EBodyPart.Chest };
    b.Player.HealthController.Parts[EBodyPart.Chest].Current = 50;
    b.Player.InventoryController.Items.Add(item);
    return item;
}
Meds Surgery(BotComponent b)
{
    var item = new Meds { Id = "surgery", AllowedPart = EBodyPart.LeftLeg };
    item.HealthEffectsComponent.Duration = 20;
    item.HealthEffectsComponent.DamageEffects[EDamageEffectType.DestroyedPart] = new();
    b.Player.HealthController.Destroyed.Add(EBodyPart.LeftLeg);
    b.Player.InventoryController.Items.Add(item);
    return item;
}
Stimulator Stim(BotComponent b, bool useful = true)
{
    var item = new Stimulator
    {
        Id = "stim",
        MedKitComponent = null,
        AllowedPart = EBodyPart.Chest,
    };
    item.HealthEffectsComponent.Duration = 2;
    item.HealthEffectsComponent.BuffSettings = new[]
    {
        new Buff
        {
            BuffType = useful ? EStimulatorBuffType.HealthRate : EStimulatorBuffType.SkillRate,
        },
    };
    b.Player.InventoryController.Items.Add(item);
    b.Player.HealthController.Parts[EBodyPart.Common].Current = 30;
    b.Memory.Health.HealthStatus = ETagStatus.BadlyInjured;
    return item;
}
MedicalTrace T(Plugin p, BotComponent b) => p.states[b].Medical.Policy.Trace!;
(Plugin, BotComponent) CloseCall()
{
    var (plugin, bot) = Setup();
    Aid(bot, "first");
    Aid(bot, "spare");
    bot.Cover.CoverInUse = null;
    var enemy = bot.EnemyController.KnownEnemies[0];
    enemy.Path.PathLength = 25;
    enemy.KnownPlaces.LastSeenPlace!.TimeSincePositionUpdated = 15;
    return (plugin, bot);
}
void CheckMedicalHearing()
{
    var (plugin, bot) = CloseCall();
    plugin.config!.TacticalMistakes.Enabled = false;
    var a = bot.EnemyController.KnownEnemies[0];
    a.EnemyProfileId = "A";
    var near = new Vector3(0, 29.99f, 0);
    plugin.Hear(a, SAINSoundType.FootStep, near);
    C(!plugin.Choose(bot).result && T(plugin, bot).Choice == "Wait"
        && T(plugin, bot).FootstepContact == "A", "hearing a nearby step makes the wounded bot wait");
    var trace = JObject.Parse(plugin.diagnostics!.Lines.Last())["medicalChoice"]!;
    C((string?)trace["footstepContact"] == "A", "medical trace names the contact heard");

    var b = new Enemy(bot) { EnemyProfileId = "B" };
    bot.EnemyController.KnownEnemies.Add(b);
    plugin.Hear(b, SAINSoundType.Sprint, near);
    plugin.Hear(a, SAINSoundType.Shot, near);
    C(plugin.Frame(bot).FootstepContact == "B", "a shot from A preserves B's sprint");
    var otherBot = new BotComponent();
    plugin.Hear(new Enemy(otherBot) { EnemyProfileId = "B" }, SAINSoundType.Shot, near);
    C(plugin.Frame(bot).FootstepContact == "B", "another observer has separate hearing for B");
    for (int i = 0; i < 20; i++)
        plugin.Hear(new Enemy(bot), SAINSoundType.FootStep, near);
    C(plugin.Frame(bot).FootstepContact == "B", "other contact reports cannot evict B's sprint");
    b.KnownPlaces.LastSeenPlace!.TimeSincePositionUpdated = 120;
    C(plugin.Frame(bot).Footsteps, "a contact exactly 120 seconds old still counts");
    b.KnownPlaces.LastSeenPlace.TimeSincePositionUpdated = 120.01f;
    C(!plugin.Frame(bot).Footsteps, "an expired contact cannot delay healing");
    b.KnownPlaces.LastSeenPlace = null;
    C(!plugin.Frame(bot).Footsteps, "a contact without a known position cannot delay healing");
    b.KnownPlaces.LastSeenPlace = new();
    bot.EnemyController.KnownEnemies.Remove(b);
    C(!plugin.Frame(bot).Footsteps, "removing a contact removes its influence immediately");
    bot.EnemyController.KnownEnemies.Add(b);
    Time.time = 14.999f;
    C(plugin.Frame(bot).Footsteps, "a sprint younger than five seconds counts");
    Time.time = 15;
    C(!plugin.Frame(bot).Footsteps, "a five-second-old sprint has expired");
    plugin.Hear(b, SAINSoundType.FootStep, new(0, 30, 0));
    C(!plugin.Frame(bot).Footsteps, "exactly 30 metres vertically is outside the radius");
    plugin.config.MedicalChoice.FootstepDistance = 31;
    plugin.config.MedicalChoice.FootstepAge = 8;
    Time.time = 21;
    C(plugin.Frame(bot).Footsteps, "custom age and radius continue to apply");
    plugin.Hear(b, SAINSoundType.Shot, near);
    C(!plugin.Frame(bot).Footsteps && plugin.Choose(bot).decision == ESelfActionType.FirstAid,
        "once both contacts have fired the same injury permits first aid");

    plugin.Authority = false;
    plugin.Hear(a, SAINSoundType.FootStep, near);
    C(!plugin.Frame(bot).Footsteps, "a non-authoritative Fika peer cannot populate hearing");
    plugin.Authority = true;
    plugin.config.MedicalChoice.Enabled = false;
    plugin.Hear(a, SAINSoundType.Sprint, near);
    C(!plugin.Frame(bot).Footsteps, "no report collected while both consumers are disabled");
    plugin.config.CoverFeint.Enabled = true;
    plugin.Hear(a, SAINSoundType.Sprint, near);
    C(plugin.Frame(bot).FootstepContact == "A", "M14 alone still collects the last sound");
}
bool originalBlock = Physics.Block;
try
{
    Physics.Block = true;
    CheckMedicalHearing();
    UnityEngine.Random.value = 0;
    var (plugin, bot) = CloseCall();
    C(!plugin.Choose(bot).result && T(plugin, bot).NaturalChoice == "FirstAid"
        && T(plugin, bot).Choice == "Wait" && T(plugin, bot).VerdictUntil == 15,
        "room for hesitation reaches the real SAIN decision hook");
    var jsonVerdict = JObject.Parse(plugin.diagnostics!.Lines.Last())["medicalChoice"]!;
    C((string?)jsonVerdict["mistakeState"] == "mistake-selected" &&
        (double?)jsonVerdict["verdictUntil"] == 15 && (bool?)jsonVerdict["started"] == false,
        "H01 records the verdict separately from medicine use");
    UnityEngine.Random.value = .99f;
    Time.time = 11;
    C(!plugin.Choose(bot).result && T(plugin, bot).MistakeRoll == null,
        "next self-action evaluation reuses the wait verdict");
    var stim = Stim(bot);
    bot.Medical.TimeSinceShot = 1;
    Time.time = 11.5f;
    C(plugin.Choose(bot).decision == ESelfActionType.Stims && T(plugin, bot).Urgent,
        "new urgent stim preempts a cached wait at the next evaluation");
    plugin.Start(bot);
    C(bot.Player.HandsController.Item == stim, "urgent stim uses the native hands");

    (plugin, bot) = CloseCall();
    UnityEngine.Random.value = 0;
    bot.Medical.TimeSinceShot = 1;
    C(plugin.Choose(bot).decision == ESelfActionType.FirstAid && T(plugin, bot).NaturalChoice == "Wait",
        "ordinary treatment can be the close second");
    UnityEngine.Random.value = .99f;
    plugin.Start(bot);
    C(T(plugin, bot).Started && bot.BotOwner.Medecine.FirstAid.Requests == 1,
        "starting honors a selected second choice with the same kit and urgency");

    (plugin, bot) = CloseCall();
    plugin.Choose(bot);
    bot.Player.HealthController.Effects.Add(new Heavy(EBodyPart.LeftArm));
    plugin.Start(bot);
    C(T(plugin, bot).Reason == "medical-new-urgent-need" && bot.BotOwner.Medecine.FirstAid.Requests == 0,
        "new heavy bleed releases a pending ordinary treatment");
    var dressing = new Meds { Id = "bleed-kit", AllowedPart = EBodyPart.LeftArm, MedKitComponent = null };
    dressing.HealthEffectsComponent.DamageEffects[EDamageEffectType.HeavyBleeding] = new();
    bot.Player.InventoryController.Items.Add(dressing);
    C(plugin.Choose(bot).result && T(plugin, bot).Item == "bleed-kit" && T(plugin, bot).MistakeRoll == null,
        "new emergency is assessed immediately without a mistake roll");

    (plugin, bot) = CloseCall();
    plugin.Choose(bot);
    var kit = bot.BotOwner.Medecine.SurgicalKit;
    kit.Bhf = true;
    kit.CurUsingMeds = Surgery(bot);
    kit._bodyPartToHeal = EBodyPart.LeftLeg;
    kit.ApplyToCurrentPart();
    var externalHands = bot.Player.HandsController;
    plugin.Start(bot);
    C(T(plugin, bot).Reason == "medical-already-in-use" && bot.BotOwner.Medecine.FirstAid.Requests == 0
        && kit.Requests == 1 && ReferenceEquals(externalHands, bot.Player.HandsController),
        "BHF can take ownership between ranking and native execution");

    (plugin, bot) = CloseCall();
    bot.Memory.Health.HealthStatus = ETagStatus.BadlyInjured;
    C(plugin.Choose(bot).decision == ESelfActionType.FirstAid && T(plugin, bot).Urgent,
        "urgent selection uses the natural best");
    bot.EnemyController.KnownEnemies[0].IsVisible = true;
    plugin.Start(bot);
    C(T(plugin, bot).Reason == "medical-start-no-longer-safe" && bot.BotOwner.Medecine.FirstAid.Requests == 0,
        "urgent care keeps its existing check when conditions change before execution");
}
finally
{
    Physics.Block = originalBlock;
    UnityEngine.Random.value = .99f;
}
foreach (var kind in new[] { "bush", "moving", "bad", "spotted", "solid" })
{
    var trial = Setup();
    Aid(trial.b);
    var cover = trial.b.Cover.CoverInUse!;
    cover.Collider.gameObject.layer = kind == "bush" ? 7 : 4;
    cover.Spotted = kind == "spotted";
    cover.CoverData.IsBad = kind == "bad";
    cover.BotInThisCover = kind != "moving";
    trial.p.Choose(trial.b);
    var trace = T(trial.p, trial.b);
    C(
        trace.ReachedCover == (kind != "moving") && trace.Cover == (kind == "solid"),
        "adapter distinguishes arrival from protection: " + kind
    );
}
var (p, b) = Setup();
Aid(b);
var choice = p.Choose(b);
C(!choice.native && choice.decision == ESelfActionType.FirstAid, "real start hook selects aid");
C(!T(p, b).Started, "selection not started");
p.Start(b);
C(
    b.BotOwner.Medecine.FirstAid.Requests == 1 && T(p, b).State == "started",
    "native action and observed hands"
);
events.AddRange(p.diagnostics!.Lines);
Time.time = 18;
p.Sample(b);
C(p.Continue(b), "8 seconds does not cancel native med at 6 second SAIN timeout");
p.Start(b);
C(b.BotOwner.Medecine.FirstAid.Requests == 1, "no second attempt during operation");
b.BotOwner.Medecine.FirstAid.Finish();
p.Sample(b);
C(
    T(p, b).State == "ended" && T(p, b).Outcome == "health-improved",
    "actual health improvement observed"
);
C(!p.Continue(b), "native continuation released at completion");
C(
    p.Choose(b).result,
    "source medical retry has no added quiet delay while treatment is still needed"
);
events.AddRange(p.diagnostics.Lines.Skip(events.Count));
foreach (var bhf in new[] { false, true })
{
    (p, b) = Setup();
    Surgery(b);
    b.BotOwner.Medecine.SurgicalKit.Bhf = bhf;
    C(p.Choose(b).decision == ESelfActionType.Surgery, "surgery selected");
    p.Start(b);
    C(b.BotOwner.Medecine.SurgicalKit.Requests == 1 && T(p, b).Started, "native surgery start");
    Time.time = 25;
    p.Sample(b);
    C(p.Continue(b), "long real surgery retained");
    b.BotOwner.Medecine.SurgicalKit.Finish();
    p.Sample(b);
    C(
        T(p, b).State == "ended" && T(p, b).Outcome == "health-improved",
        "native/BHF callback ordering"
    );
    C(b.Player.HealthController.Restores == 0, "no synthetic restoration");
    events.AddRange(p.diagnostics!.Lines);
}
foreach (
    var mode in new[]
    {
        "disabled",
        "off",
        "not-pmc",
        "dead",
        "inactive",
        "ghost",
        "authority",
        "not-sain",
        "no-world",
    }
)
{
    (p, b) = Setup();
    Aid(b);
    switch (mode)
    {
        case "disabled":
            p.config!.MedicalChoice.Enabled = false;
            break;
        case "off":
            p.config!.Enabled = false;
            break;
        case "not-pmc":
            b.Pmc = false;
            break;
        case "dead":
            b.IsDead = true;
            break;
        case "inactive":
            b.BotActive = false;
            break;
        case "ghost":
            p.IsGhost = true;
            break;
        case "authority":
            p.Authority = false;
            break;
        case "not-sain":
            b.SAINLayersActive = false;
            break;
        default:
            p.world = null;
            break;
    }
    C(p.Choose(b).native, mode + " leaves native untouched");
    C(b.BotOwner.Medecine.FirstAid.Requests == 0, mode + " no side effect");
}
foreach (var mode in new[] { "refuse", "removed", "unusable", "empty", "cooldown" })
{
    (p, b) = Setup();
    var item = Aid(b);
    p.Choose(b);
    switch (mode)
    {
        case "refuse":
            b.BotOwner.Medecine.FirstAid.Refuse = true;
            break;
        case "removed":
            item.Removed = true;
            break;
        case "unusable":
            item.Applicable = false;
            break;
        case "empty":
            item.MedKitComponent!.HpResource = 0;
            break;
        case "hit":
            b.Medical.TimeSinceShot = 0;
            break;
        case "moving":
            b.Player.Velocity = new(2, 0, 0);
            break;
        case "cooldown":
            b.BotOwner.Medecine.FirstAid._nextPosibleUseTime = 20;
            break;
        case "door":
            b.BotOwner.DoorOpener.Interacting = true;
            break;
        case "reload":
            b.BotOwner.WeaponManager.Reload.Reloading = true;
            break;
        default:
            b.Grenade.GrenadeDangerPoint = new();
            break;
    }
    p.Start(b);
    C(!T(p, b).Started && T(p, b).State == "ended", mode + " start refused without false start");
    C(b.BotOwner.Medecine.FirstAid.Requests == (mode == "refuse" ? 1 : 0), mode + " bounded start");
    Time.time = 11;
    C(
        p.Choose(b).result == (mode == "refuse"),
        mode + " native eligibility determines retry without extra delay"
    );
    events.AddRange(p.diagnostics!.Lines);
}
(p, b) = Setup();
Aid(b);
b.BotOwner.Medecine.FirstAid.DelayHands = true;
p.Choose(b);
p.Start(b);
C(T(p, b).State == "requested" && !T(p, b).Started, "using flag not proof of hands");
Time.time = 11;
b.BotOwner.Medecine.FirstAid.Draw();
p.Sample(b);
C(T(p, b).Started, "asynchronous real draw observed");
(p, b) = Setup();
Aid(b);
b.BotOwner.Medecine.FirstAid.DelayHands = true;
p.Choose(b);
p.Start(b);
Time.time = 16;
p.Sample(b);
C(
    !p.states[b].Medical.Fault && T(p, b).Reason == "medical-native-operation-stalled",
    "missing observation logged without disabling future medicine"
);
C(b.BotOwner.Medecine.FirstAid.Using, "do not cancel another mod's operation");
(p, b) = Setup();
Stim(b);
C(p.Choose(b).decision == ESelfActionType.Stims, "single-use resource-less useful stim selected");
p.Start(b);
C(T(p, b).Started && b.BotOwner.Medecine.Stimulators.Requests == 1, "native stim used");
Time.time = 14;
C(p.Continue(b), "stim not cancelled at native 3 second deadline");
events.AddRange(p.diagnostics!.Lines);
(p, b) = Setup();
Stim(b, false);
C(!p.Choose(b).result, "skill-only stim not a heal");
(p, b) = Setup();
Aid(b, "ordinary");
var bandage = new Meds
{
    Id = "heavy-bandage",
    AllowedPart = EBodyPart.LeftArm,
    MedKitComponent = null,
};
bandage.HealthEffectsComponent.DamageEffects[EDamageEffectType.HeavyBleeding] = new();
b.Player.InventoryController.Items.Add(bandage);
b.Player.HealthController.Effects.Add(new Heavy(EBodyPart.LeftArm));
b.EnemyController.KnownEnemies[0].IsVisible = false;
p.Choose(b);
C(
    T(p, b).Item == "heavy-bandage" && T(p, b).Part == "LeftArm",
    "bandage on actual bleed ahead of ordinary HP"
);
p.Start(b);
C(
    b.BotOwner.Medecine.FirstAid.CurUsingMeds == bandage
        && b.BotOwner.Medecine.FirstAid._bodyPartToHeal == EBodyPart.LeftArm,
    "selected usable item/part sent to native"
);
foreach (bool heavy in new[] { true, false })
{
    (p, b) = Setup();
    b.EnemyController.KnownEnemies.Clear();
    var health = b.Player.HealthController;
    health.Destroyed.Add(EBodyPart.LeftArm);
    health.Parts[EBodyPart.LeftArm].Current = 0;
    health.Parts[EBodyPart.Chest].Current = 45;
    var damage = heavy ? EDamageEffectType.HeavyBleeding : EDamageEffectType.LightBleeding;
    int cost = heavy ? 170 : 30;
    var low = new MedKit { Id = "used-AFAK" };
    var full = new MedKit { Id = "full-AFAK" };
    foreach (var kit in new[] { low, full })
    {
        kit.HealthEffectsComponent.DamageEffects[damage] = new() { Cost = cost };
        b.Player.InventoryController.Items.Add(kit);
    }
    low.MedKitComponent!.HpResource = heavy ? 111 : 29;
    full.MedKitComponent!.HpResource = 400;
    health.Effects.Add(heavy ? new Heavy(EBodyPart.LeftArm) : new Light(EBodyPart.LeftArm));
    var aid = p.Supplies(b).Options.Single(o => o.Action == "FirstAid");
    C(aid.Item == full.Id && aid.Part == "LeftArm" && aid.Need == (heavy ? "heavy-bleed" : "light-bleed"),
        "charged kit treats bleeding on blacked arm ahead of chest HP");
    full.Applicable = false;
    C(p.Supplies(b).Options.Single(o => o.Action == "FirstAid").Part == "Chest",
        "native refusal and exhausted charge do not enable HP healing on blacked arm");
    full.Applicable = true;
    C(p.Choose(b).decision == ESelfActionType.FirstAid, "bleeding kit selected");
    p.Start(b);
    C(b.BotOwner.Medecine.FirstAid.CurUsingMeds == full && T(p, b).TargetDestroyedAtStart == true,
        "native request targets blacked arm with correct kit");
    health.Effects.Clear();
    b.BotOwner.Medecine.FirstAid.Finish(improve: false);
    p.Sample(b);
    C(T(p, b).TargetBleedCleared == true && health.Destroyed.Contains(EBodyPart.LeftArm)
        && health.Parts[EBodyPart.LeftArm].Current == 0, "hemostasis recorded without restoring the limb");
    C(p.Supplies(b).Options.Single(o => o.Action == "FirstAid").Part == "Chest",
        "without a bleed, first aid skips the blacked arm");
}
{
    (p, b) = Setup();
    b.EnemyController.KnownEnemies.Clear();
    var hybridKit = Surgery(b);
    hybridKit.HealthEffectsComponent.DamageEffects[EDamageEffectType.HeavyBleeding] = new() { Cost = 40 };
    b.Player.HealthController.Effects.Add(new Heavy(EBodyPart.LeftLeg));
    var options = p.Supplies(b).Options;
    C(options.Any(o => o.Action == "Surgery" && o.Item == hybridKit.Id)
        && options.Any(o => o.Action == "FirstAid" && o.Item == hybridKit.Id && o.Need == "heavy-bleed"),
        "multi-purpose kit can supply surgery and first aid independently");
    b.BotOwner.Medecine.SurgicalKit._nextPosibleUseTime = Time.time + 30;
    C(p.Choose(b).decision == ESelfActionType.FirstAid, "surgery cooldown cannot veto a dressing");
    hybridKit.MedKitComponent!.HpResource = 39;
    p.Start(b);
    C(b.BotOwner.Medecine.FirstAid.Requests == 0 && T(p, b).Reason == "medical-item-no-longer-usable",
        "charge lost between selection and execution releases pending first aid");
}
foreach (var mode in new[] { "new", "near", "no-cover", "not-protective", "incomplete", "feet" })
{
    (p, b) = Setup();
    Surgery(b);
    var e = b.EnemyController.KnownEnemies[0];
    switch (mode)
    {
        case "new":
            b.EnemyController.KnownEnemies.Add(
                new(b)
                {
                    EnemyProfileId = "fresh",
                    KnownPlaces = new()
                    {
                        LastSeenPlace = new()
                        {
                            TimeSincePositionUpdated = 2,
                            Position = new(100, 0, 0),
                        },
                    },
                }
            );
            break;
        case "near":
            e.KnownPlaces.LastSeenPlace!.Position = new(30, 0, 0);
            break;
        case "no-cover":
            b.Cover.CoverInUse = null;
            break;
        case "not-protective":
            p.Protection = 0;
            break;
        case "incomplete":
            for (int i = 0; i < 17; i++)
                b.EnemyController.KnownEnemies.Add(new(b));
            break;
        case "feet":
            e.KnownPlaces.LastHeardPlace = new()
            {
                Position = new(20, 0, 0),
                TimeSincePositionUpdated = 0,
            };
            p.Hear(e, SAINSoundType.FootStep, e.KnownPlaces.LastHeardPlace.Position);
            break;
    }
    bool selected = p.Choose(b).result;
    C(
        mode is "new" or "no-cover" or "feet" ? !selected : selected,
        mode + " follows source utility without extra cover/completeness veto"
    );
    if (mode == "new")
        C(T(p, b).Contacts == 2, "fresh other threat represented");
}
(p, b) = Setup();
Aid(b);
p.Choose(b);
p.Start(b);
p.IsGhost = true;
p.Sample(b);
C(
    T(p, b).State == "ended" && p.states[b].Medical.Item == null,
    "ghost clears owned state without cancelling native"
);
(p, b) = Setup();
Aid(b);
p.Choose(b);
p.Start(b);
p.End(b);
C(
    p.states[b].Medical.Item == null && !p.states[b].Medical.Policy.Active,
    "raid cleanup drops item"
);
b.BotOwner.Medecine.FirstAid.Finish();
C(!p.states[b].Medical.Callback, "late callback token invalidated");
(p, b) = Setup();
Aid(b);
p.doorBudget.Available = false;
C(
    p.Choose(b).result && b.BotOwner.Medecine.FirstAid.Requests == 0,
    "diagnostic/path budget cannot veto medical selection"
);
(p, b) = Setup();
Aid(b);
p.Choose(b);
b.BotOwner.Medecine.FirstAid.Throw = true;
p.Execute(b, "DoFirstAid");
C(p.states[b].Medical.Fault && T(p, b).State == "ended", "native exception no retry spam");

(p, b) = Setup();
Surgery(b);
p.Choose(b);
Time.time = 11;
C(
    !p.SurgeryAction(b) && b.BotOwner.Medecine.SurgicalKit.Requests == 1,
    "surgery action executes only native kit"
);
p.SurgeryAction(b);
C(b.BotOwner.Medecine.SurgicalKit.Requests == 1, "surgery update cannot repeat use");
p.RestoreOnStop(b);
C(b.Player.HealthController.Restores == 0, "surgery Stop cannot restore limbs for free");
C(!p.states[b].Medical.SurgeryOwned, "Stop releases ownership");
(p, b) = Setup();
p.RestoreOnStop(b);
C(b.Player.HealthController.Restores == 1, "unowned surgery Stop unchanged");
(p, b) = Setup();
b.Player.HealthController.Destroyed.Add(EBodyPart.LeftLeg);
C(p.RestoreOnStop(b), "native restore success bool retained");
C(!p.RestoreOnStop(b), "native restore refusal bool retained");
(p, b) = Setup();
Surgery(b);
p.Choose(b);
Time.time = 11;
p.SurgeryAction(b);
C(
    !p.RestoreOnStop(b) && b.Player.HealthController.Restores == 0,
    "owned surgery returns false without restoring"
);
(p, b) = Setup();
Aid(b);
p.Choose(b);
b.Player.HealthController.Parts[EBodyPart.Chest].Current = 100;
p.Start(b);
C(
    T(p, b).Reason == "medical-need-resolved" && b.BotOwner.Medecine.FirstAid.Requests == 0,
    "resolved injury not treated again"
);
var nativeRestore = typeof(ActiveHealthController).GetMethod("RestoreBodyPart")!;
var code = p.Transform(
        new[]
        {
            new HarmonyLib.CodeInstruction(System.Reflection.Emit.OpCodes.Callvirt, nativeRestore),
        }
    )
    .ToArray();
C(
    code.Length == 2
        && code[0].opcode == System.Reflection.Emit.OpCodes.Ldarg_0
        && ((System.Reflection.MethodInfo)code[1].operand).Name == "NativeRestoreAfterSurgery",
    "scoped surgery Stop transpiler"
);
try
{
    p.Transform(Array.Empty<HarmonyLib.CodeInstruction>()).ToArray();
    C(false, "missing native restore accepted");
}
catch (InvalidOperationException)
{
    checks++;
}

// A native brain/BHF starts the operation before COMET has any per-bot state.
foreach (bool bhf in new[] { false, true })
{
    (p, b) = Setup();
    p.config!.SurgeryInterruption.Enabled = true;
    b.EnemyController.KnownEnemies.Clear();
    var item = Surgery(b);
    var kit = b.BotOwner.Medecine.SurgicalKit;
    kit.Bhf = bhf;
    kit.CurUsingMeds = item;
    kit._bodyPartToHeal = EBodyPart.LeftLeg;
    int callbacks = 0;
    Action callback = () => callbacks++;
    kit.ApplyToCurrentPart(callback);
    var originalHands = b.Player.HandsController;
    C(p.Continue(b), "external surgery adopted without an existing COMET state");
    C(
        p.states[b].Medical.ExternalSurgery
            && p.states[b].Medical.Interruption.Trace!.Origin == "native",
        "native origin is logged"
    );
    C(
        !p.SurgeryAction(b)
            && kit.Requests == 1
            && ReferenceEquals(callback, kit.Done)
            && ReferenceEquals(originalHands, b.Player.HandsController),
        "adoption keeps native/BHF hands, kit and callback"
    );
    kit.Finish();
    C(!p.Continue(b) && callbacks == 1, "external completion observed for both callback orders");
    C(!p.states[b].Medical.ExternalSurgery && kit.Requests == 1, "completed surgery not restarted");
    p.RestoreOnStop(b);
    C(
        b.Player.HealthController.Restores == 0,
        "external surgery exit cannot restore unrelated limbs"
    );
    events.AddRange(p.diagnostics!.Lines);
}
(p, b) = Setup();
p.config!.SurgeryInterruption.Enabled = true;
b.EnemyController.KnownEnemies.Clear();
var externalItem = Surgery(b);
var externalKit = b.BotOwner.Medecine.SurgicalKit;
externalKit.CurUsingMeds = externalItem;
externalKit._bodyPartToHeal = EBodyPart.LeftLeg;
externalKit.ApplyToCurrentPart();
C(p.Continue(b), "external surgery safe before contact");
b.EnemyController.KnownEnemies.Add(new(b) { IsVisible = true });
C(
    !p.Continue(b) && externalKit.Cancels == 1,
    "source danger asks native CancelCurrent exactly once"
);
p.Continue(b);
p.SurgeryAction(b);
C(
    externalKit.Cancels == 1 && externalKit.Requests == 1,
    "no cancel spam or duplicate kit during native return"
);
externalKit.Finish(false);
p.Continue(b);
C(
    !p.states[b].Medical.ExternalSurgery
        && b.Player.HealthController.IsBodyPartDestroyed(EBodyPart.LeftLeg),
    "cancelled external operation does not heal artificially"
);
foreach (string mode in new[] { "scav", "client", "ghost", "off", "medical-off" })
{
    (p, b) = Setup();
    var item = Surgery(b);
    var kit = b.BotOwner.Medecine.SurgicalKit;
    kit.CurUsingMeds = item;
    kit._bodyPartToHeal = EBodyPart.LeftLeg;
    kit.ApplyToCurrentPart();
    if (mode == "scav")
        b.Pmc = false;
    if (mode == "client")
        p.Authority = false;
    if (mode == "ghost")
        p.IsGhost = true;
    if (mode == "off")
        p.config!.Enabled = false;
    if (mode == "medical-off")
        p.config!.MedicalChoice.Enabled = false;
    p.Continue(b);
    C(
        !p.states.ContainsKey(b) && kit.Cancels == 0 && kit.Requests == 1,
        mode + ": native operation remains untouched"
    );
}
(p, b) = Setup();
b.EnemyController.KnownEnemies.Clear();
externalItem = Surgery(b);
externalKit = b.BotOwner.Medecine.SurgicalKit;
externalKit.CurUsingMeds = externalItem;
externalKit._bodyPartToHeal = EBodyPart.LeftLeg;
externalKit.ApplyToCurrentPart();
p.Continue(b);
b.EnemyController.KnownEnemies.Add(new(b) { IsVisible = true });
C(
    p.Continue(b) && externalKit.Cancels == 0,
    "disabled interruption module stays disabled for adopted operations"
);
b.EnemyController.KnownEnemies.Clear();
externalKit._bodyPartToHeal = EBodyPart.RightLeg;
b.Player.HealthController.Destroyed.Add(EBodyPart.RightLeg);
p.Continue(b);
C(
    p.states[b].Medical.Interruption.Trace!.Part == "RightLeg" && externalKit.Requests == 1,
    "native next-part transition tracked without starting another operation"
);
p.IsGhost = true;
p.Sample(b);
C(
    !p.states[b].Medical.ExternalSurgery && externalKit.Cancels == 0,
    "ghost handoff releases monitoring without touching native operation"
);
// SeekCoverAction.Stop clears SAIN's current point before the surgery action runs.
(p, b) = Setup();
Surgery(b);
C(p.Choose(b).decision == ESelfActionType.Surgery, "relaxed surgery chosen at occupied cover");
b.Cover.CoverInUse = null;
Time.time += 1;
p.SurgeryAction(b);
C(b.BotOwner.Medecine.SurgicalKit.Requests == 1 && T(p, b).Started,
    "releasing the seek-cover action at the same spot must start the selected kit");
p.SurgeryAction(b);
C(b.BotOwner.Medecine.SurgicalKit.Requests == 1 && p.states[b].Medical.PendingCover == null,
    "a second update cannot reuse the cover or start a second kit");
var startLog = JObject.Parse(p.diagnostics!.Lines.Last())["medicalChoice"]!;
C((string?)startLog["startCover"] == "retained" && (string?)startLog["surgeryAtSelection"] == "relaxed-cover"
    && (string?)startLog["surgeryAtStart"] == "relaxed-cover", "log explains both gates and the handoff");
events.AddRange(p.diagnostics.Lines);

foreach (var (change, reason) in new (Action<BotComponent, Point>, string)[]
{
    ((bot, point) => bot.Position = new(6, 0, 0), "cover-not-reached"),
    ((bot, point) => point.Spotted = true, "cover-not-reached"),
    ((bot, point) => point.CoverData.IsBad = true, "cover-not-reached"),
    ((bot, point) => point.Collider = null!, "cover-not-reached"),
    ((bot, point) => bot.Cover.CoverInUse = new() { BotInThisCover = false }, "cover-not-reached"),
    ((bot, point) => bot.Cover.CoverPoint_MovingTo = new(), "cover-not-reached"),
    ((bot, point) => bot.EnemyController.KnownEnemies[0].IsVisible = true, "enemy-visible"),
    ((bot, point) => bot.EnemyController.KnownEnemies[0].Path.PathLength = 1, "enemy-too-close"),
    ((bot, point) => bot.EnemyController.KnownEnemies[0].TimeSinceSeen = 1, "recent-sighting"),
    ((bot, point) => bot.EnemyController.KnownEnemies[0].KnownPlaces.LastSeenPlace!.TimeSincePositionUpdated = 1, "recent-contact"),
    ((bot, point) => bot.BotOwner.Medecine.FirstAid.IsBleeding = true, "bleeding"),
    ((bot, point) => bot.BotOwner.Medecine.SurgicalKit.HaveWork = false, "no-native-surgery-work"),
    ((bot, point) => bot.BotOwner.WeaponManager.Grenades.ThrowindNow = true, "throwing-grenade"),
    ((bot, point) => bot.BotOwner.Medecine.SurgicalKit._nextPosibleUseTime = 12, "kit-cooldown"),
})
{
    (p, b) = Setup();
    Surgery(b);
    C(p.Choose(b).result, "select before " + reason);
    var oldCover = b.Cover.CoverInUse!;
    b.Cover.CoverInUse = null;
    Time.time += 1;
    change(b, oldCover);
    p.SurgeryAction(b);
    C(T(p, b).SurgeryAtStart == reason && T(p, b).State == "ended"
        && b.BotOwner.Medecine.SurgicalKit.Requests == 0 && p.states[b].Medical.PendingCover == null,
        "genuine refusal releases the pending operation: " + reason);
    // The old cover cannot admit a new selection. Remove the danger and enter
    // a new, valid cover: the normal decision tick can start the same kit again.
    var enemy = b.EnemyController.KnownEnemies[0];
    enemy.IsVisible = false;
    enemy.Path.PathLength = 60;
    enemy.TimeSinceSeen = 100;
    enemy.KnownPlaces.LastSeenPlace!.TimeSincePositionUpdated = 30;
    b.BotOwner.Medecine.FirstAid.IsBleeding = false;
    b.BotOwner.Medecine.SurgicalKit.HaveWork = true;
    b.BotOwner.WeaponManager.Grenades.ThrowindNow = false;
    b.BotOwner.Medecine.SurgicalKit._nextPosibleUseTime = Time.time;
    b.Cover.CoverInUse = null;
    b.Cover.CoverPoint_MovingTo = null;
    C(!p.Choose(b).result && T(p, b).SurgeryAtSelection == "cover-not-reached", "old point not reused for selection");
    Time.time += .5f;
    b.Cover.CoverInUse = new() { Position = b.Position };
    C(p.Choose(b).result, "same limb can be retried at the next evaluation after " + reason);
    p.Start(b);
    C(T(p, b).Started && b.BotOwner.Medecine.SurgicalKit.Requests == 1, "retry reaches the real kit once");
}

// Even a previously quiet raid can gain a contact while the bot is getting ready.
(p, b) = Setup();
b.EnemyController.KnownEnemies.Clear();
b.Cover.CoverInUse = null;
Surgery(b);
C(p.Choose(b).result && T(p, b).SurgeryAtSelection == "strict-area-clear", "clear area permits surgery in the open");
b.EnemyController.KnownEnemies.Add(new(b) { IsVisible = true });
p.Start(b);
C(T(p, b).SurgeryAtStart == "enemy-visible" && !T(p, b).Started, "new contact rechecked on the no-enemy branch");

(p, b) = CloseCall();
Aid(b, "third");
b.Memory.Health.HealthStatus = ETagStatus.BadlyInjured;
b.EnemyController.KnownEnemies[0].Path.PathLength = 19;
b.EnemyController.KnownEnemies[0].KnownPlaces.LastSeenPlace!.TimeSincePositionUpdated = 10;
Physics.Block = true;
C(p.Choose(b).decision == ESelfActionType.FirstAid, "available supplies favor urgent aid over waiting");
p.Start(b);
C(T(p, b).Started, "same supplies and danger give the same first aid decision at execution");
C(T(p, b).StartTreatmentScore > T(p, b).StartWaitScore, "logs report comparable scores at execution");
(p, b) = CloseCall();
var spare = Aid(b, "third");
b.Memory.Health.HealthStatus = ETagStatus.BadlyInjured;
b.EnemyController.KnownEnemies[0].Path.PathLength = 19;
b.EnemyController.KnownEnemies[0].KnownPlaces.LastSeenPlace!.TimeSincePositionUpdated = 10;
C(p.Choose(b).result, "initial supplies favor aid");
b.Player.InventoryController.Items.Remove(spare);
p.Start(b);
C(T(p, b).Reason == "medical-start-no-longer-safe" && T(p, b).StartTreatmentScore < T(p, b).StartWaitScore,
    "real inventory change is reflected when re-ranking the pending first aid");
foreach (bool foliage in new[] { false, true })
{
    (p, b) = CloseCall();
    var extra = Aid(b, "third");
    var cover = new Point();
    if (foliage)
        cover.Collider.gameObject.layer = 7;
    b.Cover.CoverInUse = cover;
    b.Memory.Health.HealthStatus = ETagStatus.BadlyInjured;
    b.EnemyController.KnownEnemies[0].Path.PathLength = 19;
    b.EnemyController.KnownEnemies[0].KnownPlaces.LastSeenPlace!.TimeSincePositionUpdated = 10;
    C(p.Choose(b).result, "aid chosen before handing off cover");
    b.Cover.CoverInUse = null;
    b.Player.InventoryController.Items.Remove(extra);
    p.Start(b);
    C(T(p, b).StartCover == "retained" && T(p, b).Started == !foliage,
        "first aid rechecks the same spot and only solid cover earns protection");
}
Physics.Block = originalBlock;

string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
Directory.CreateDirectory(Path.Combine(root, "artifacts"));
File.WriteAllLines(Path.Combine(root, "artifacts/h01-adapter-events.jsonl"), events);
var fixture = events
    .Select(JObject.Parse)
    .First(e =>
        e["medicalChoice"]!["state"]!.Value<string>() == "started"
        && e["medicalChoice"]!["choice"]!.Value<string>() == "Surgery"
    );
File.WriteAllText(Path.Combine(root, "artifacts/h01-medical-choice.json"), fixture.ToString());
Console.WriteLine(
    $"H01 production adapter: {checks} checks passed; {events.Count} actual JSON events"
);

H02Tests.Run();
