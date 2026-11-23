using System.Runtime.CompilerServices;
using Comet.Core;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using SAIN;
using SAIN.Components;
using SAIN.Components.PlayerComponentSpace;
using SAIN.Models.Enums;
using SAIN.SAINComponent.Classes;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;

namespace Comet.Client;

public sealed partial class Plugin
{
    private sealed class OpeningAim(Weapon weapon, OpeningShot shot)
    {
        internal readonly Weapon Weapon = weapon;
        internal readonly OpeningShot Shot = shot;
        internal int AimFrame = -1;
    }

    // SAIN creates a separate Enemy for each observer. Once forgotten/disposed, the
    // contact and its one-shot state are collected together, including across raids.
    private readonly ConditionalWeakTable<Enemy, OpeningAim> openingShots = new();

    private static void PatchOpeningShot(Harmony h)
    {
        h.Patch(
            AccessTools.Method(typeof(SAINShootData), "GetAimTarget"),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(OpeningAimPoint))
        );
        h.Patch(
            AccessTools.Method(typeof(SAINShootData), "ShootWhenAimComplete"),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(OpeningTrigger))
        );
        h.Patch(
            AccessTools.Method(typeof(PlayerComponent), nameof(PlayerComponent.OnMakingShot)),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(OpeningObservedShot)) { priority = Priority.Last }
        );
    }

    private static void OpeningAimPoint(Enemy enemy, ref Vector3? __result)
    {
        var p = Instance;
        if (p == null || enemy == null || !enemy.IsAI)
            return;
        var now = Time.time;
        var bot = enemy.Bot;
        p.openingShots.TryGetValue(enemy, out var aim);
        if (aim?.Shot.Finished == true)
            return;
        if (aim == null && (!__result.HasValue || !enemy.Seen
            || now - enemy.Vision.TimeFirstSeen > OpeningShot.FreshSeconds
            || enemy.EnemyInfo.PersonalLastShootTime >= 0f || bot.BotOwner.ShootData.Shooting))
            return;

        var weapon = bot.Player.HandsController?.Item as Weapon;
        bool eligible = __result.HasValue && enemy.IsVisible && enemy.CanShoot
            && weapon != null && (aim == null || ReferenceEquals(weapon, aim.Weapon))
            && p.IsOpeningOpportunity(bot, enemy, weapon);
        var sightStart = enemy.Vision.VisibleStartTime;
        if (aim != null && !aim.Shot.Continue(now, sightStart, eligible))
            return;
        if (!eligible)
            return;

        var global = SAINPlugin.LoadedPreset.GlobalSettings.Aiming.AimTarget;
        if (aim == null)
        {
            var preset = bot.Info.FileSettings.Aiming;
            var weights = preset.OverrideAimTargetWeights ? preset.AimTargetWeights : global.Weights;
            bool head = global.Enabled && preset.AimForHead && weights.Head > 0
                && enemy.RealDistance >= global.CloseRangeTorsoDistance
                && enemy.Vision.EnemyParts.Parts.TryGetValue(EBodyPart.Head, out var part)
                && part.CanBeSeen && part.ShootPoint.HasValue
                && MyExtensions.RandomBool(preset.AimForHeadChance);
            aim = new OpeningAim(weapon!, new OpeningShot(now, sightStart, head, enemy.RealDistance));
            p.openingShots.Add(enemy, aim);
        }
        if (aim.Shot.Head)
        {
            if (!enemy.Vision.EnemyParts.Parts.TryGetValue(EBodyPart.Head, out var head)
                || !head.CanBeSeen || head.ShootPoint is not Vector3 shootPoint)
            {
                aim.Shot.Finish();
                return;
            }
            __result = Vector3.Lerp(head.Transform.position, shootPoint, global.AimPointSpread);
        }
        aim.AimFrame = Time.frameCount;
    }

    private bool IsOpeningOpportunity(BotComponent bot, Enemy enemy, Weapon weapon)
    {
        if (config?.Enabled != true || !bot.Info.Profile.IsPMC || !bot.Player.IsAI
            || !Authority || bot.GameEnding || bot.IsDead || !bot.BotActive
            || !bot.SAINLayersActive || !ReferenceEquals(bot.GoalEnemy, enemy)
            || bot.ManualShoot.Reason != EShootReason.None || !ContactSafety.Allows(bot, enemy)
            || Ghost(bot.ProfileId) || bot.BotOwner.WeaponManager.UnderbarrelLauncherController.IsActive
            || weapon.SelectedFireMode is not (Weapon.EFireMode.single or Weapon.EFireMode.fullauto
                or Weapon.EFireMode.semiauto or Weapon.EFireMode.doubleaction)
            || weapon.WeapClass == "grenadeLauncher")
            return false;

        // Only cues available to this PMC: cached vision, target orientation and incoming fire.
        var toBot = bot.Position - enemy.EnemyPosition;
        toBot.y = 0f;
        var look = enemy.EnemyTransform.LookDirection;
        look.y = 0f;
        if (toBot.sqrMagnitude < .001f || look.sqrMagnitude < .001f)
            return false;
        bool pressure = bot.BotOwner.Memory.IsUnderFire || bot.Suppression.IsSuppressed
            || enemy.Status.ShotAtMeRecently || enemy.Status.ShotMeRecently
            || bot.EnemyController.VisibleEnemies.Count > 1 || bot.Player.IsSprintEnabled
            || (bot.Medical.TimeLastShot > 0f && bot.Medical.TimeSinceShot < 3f);
        return OpeningShot.LowThreat(Vector3.Dot(toBot.normalized, look.normalized), pressure);
    }

    private static void OpeningTrigger(Enemy Enemy, BotComponent bot, ref bool AimComplete)
    {
        var p = Instance;
        if (p == null || !Enemy.IsAI || !p.openingShots.TryGetValue(Enemy, out var aim)
            || aim.Shot.Finished || aim.AimFrame != Time.frameCount || bot.BotOwner.ShootData.Shooting)
            return;
        // Use the same look direction and ballistic aim point as SAIN, with a narrower
        // tolerance for this opening. Timeout always returns control to native shooting.
        var toAim = bot.BotOwner.AimingManager.CurrentAiming.EndTargetPoint - bot.Transform.WeaponData.WeaponRoot;
        var look = bot.PlayerComponent.CharacterController.CurrentControlLookDirection;
        bool aligned = AimComplete && Vector3.Dot(look, toAim.normalized) >= .997f;
        AimComplete = aim.Shot.AllowTrigger(Time.time, AimComplete, aligned);
    }

    private static void OpeningObservedShot(PlayerComponent __instance, IWeapon __0)
    {
        var p = Instance;
        var bot = __instance.BotComponent;
        var target = bot?.Shoot?.LastShotEnemy;
        if (p == null || bot == null || target == null || !target.IsAI
            || !p.openingShots.TryGetValue(target, out var aim) || aim.Shot.Finished
            || !ReferenceEquals(aim.Weapon, __0) || bot.ManualShoot.Reason != EShootReason.None
            || !bot.BotOwner.ShootData.Shooting || !aim.Shot.ObserveShot(bot.BotOwner.ShootData.LastTriggerPressd))
            return;
        bot.Shoot.EndShoot();
        if (p.config?.QuietLogging == false)
            p.Logger.LogInfo($"Opening shot fired by {bot.ProfileId} at {target.EnemyProfileId} (aim: {(aim.Shot.Head ? "head" : "SAIN")}, settle target: {aim.Shot.SettleSeconds:F2}s)");
    }
}
