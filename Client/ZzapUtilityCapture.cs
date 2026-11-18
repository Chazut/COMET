using Comet.Core;
using EFT;
using SAIN.Components;
using SAIN.Models.Enums;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SAIN.SAINComponent.Classes.WeaponFunction;
using UnityEngine;

namespace Comet.Client;

// Evidence used by the original utility functions. Called only for utility scoring,
// not by every feature that takes a general contact snapshot.
internal static class ZzapUtilityCapture
{
    internal static void Fill(BotComponent bot, Enemy enemy, Situation s)
    {
        s.Health = bot.Memory.Health.HealthStatus switch {
            ETagStatus.Healthy => 1, ETagStatus.Injured => .7, ETagStatus.BadlyInjured => .35, _ => .1 };
        s.Ammo = SAINBotSuppressClass.CalcAmmoRatio(bot.BotOwner, out _);
        s.Weakness = ZzapEnemyWeakness.Read(bot, enemy, out _);
        s.PathLength = enemy.Path.PathLength;
        s.Busy = enemy.Status.VulnerableAction != EEnemyAction.None;
        s.GrenadeOut = enemy.Status.VulnerableAction == EEnemyAction.HasGrenade;
        s.HoldsAngle = enemy.Status.TimeLastShotAtMe > 0 && Time.time - enemy.Status.TimeLastShotAtMe < 5;
        s.Looking = enemy.EnemyLookingAtMe;
        s.Indoor = enemy.EnemyPlayer?.Environment == EnvironmentType.Indoor;
        s.UnderFire = bot.BotOwner.Memory.IsUnderFire;
        s.Frozen = bot.Decision.CurrentCombatDecision == ECombatDecision.Freeze;
        var h = enemy.KnownPlaces.LastHeardPlace;
        s.Coming = h != null && h.TimeSincePositionUpdated < 3
            && (h.SoundType == SAINSoundType.FootStep || h.SoundType == SAINSoundType.Sprint)
            && Vector3.Distance(h.Position, bot.Position) < s.KnownDistance + 1;
        var med = bot.BotOwner.Medecine;
        s.CanHeal = (med?.FirstAid?.Damaged == true && med.FirstAid.HaveSmth2Use) || med?.SurgicalKit?.HaveWork == true;
        FillNumbers(bot, enemy, s);
        s.OpenGround = false;
        if (!s.Visible && !s.InCover)
        {
            int enclosure = 0;
            for (int i = 0; i < 8; i++)
            {
                float a = i * Mathf.PI / 4;
                if (Physics.Raycast(bot.Position + Vector3.up * 1.1f, new Vector3(Mathf.Sin(a), 0, Mathf.Cos(a)),
                    2.5f, LayersMaskController.HighPolyWithTerrainMask)) enclosure++;
            }
            s.OpenGround = enclosure < 3 && !Physics.Linecast(
                new Vector3((float)s.KnownX, (float)s.KnownY + 1.4f, (float)s.KnownZ),
                bot.Position + Vector3.up, LayersMaskController.HighPolyWithTerrainMask);
        }
    }

    internal static void FillNumbers(BotComponent bot, Enemy enemy, Situation s)
    {
        int mates = 0, visible = 0, others = 0;
        if (bot.Squad?.Members != null) foreach (var m in bot.Squad.Members.Values)
        {
            if (m == null || ReferenceEquals(m, bot) || m.IsDead || m.GoalEnemy?.EnemyProfileId != enemy.EnemyProfileId) continue;
            if ((m.Position - bot.Position).sqrMagnitude <= 900) mates++;
            if (m.GoalEnemy.IsVisible) visible++;
        }
        bool expectsGroup = s.RecognizedEnemy.ExpectsGroup || s.LocalGroupCue.ExpectsGroup;
        int nearbyContacts = 0;
        var contact = new Vector3((float)s.KnownX, (float)s.KnownY, (float)s.KnownZ);
        if (bot.EnemyController?.KnownEnemies != null) foreach (var e in bot.EnemyController.KnownEnemies)
        {
            if (e == null || ReferenceEquals(e, enemy) || e.TimeSinceLastKnownUpdated >= 20) continue;
            if ((e.EnemyPosition - enemy.EnemyPosition).sqrMagnitude < 400) others++;
            if (expectsGroup && e.LastKnownPosition is Vector3 known && (known - contact).sqrMagnitude <= 35 * 35) nearbyContacts++;
        }
        if (expectsGroup || s.RecognizedEnemy.Role == ContactRole.Scav)
            s.EnemyRole = new EnemyRoleAssessment(s.RecognizedEnemy, s.LocalGroupCue, EnemyRoleCapture.RaidProgress(), nearbyContacts, mates);
        s.Numbers = Mathf.Clamp(mates - others, -2, 2) / 2d;
        s.VisibleMates = Mathf.Clamp(visible, 0, 2) / 2d;
    }
}
