// Adapted from R_F's SAIN zzap fbb86b838e0fa692b759b2c0b11fc30158a53876, SquadStorm.cs.
// The original SAIN DLL remains a dependency; these are the fork's additional decisions.
using System.Collections.Generic;
using EFT;
using EFT.InventoryLogic;
using SAIN.Components;
using SAIN.Models.Enums;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SAIN.SAINComponent.Classes.WeaponFunction;
using UnityEngine;

namespace Comet.Client;

internal static class ZzapEnemyWeakness
{
    private static readonly List<ArmorComponent> _armor = new();

    internal static float Read(BotComponent b, Enemy e, out string[] why)
    {
        var parts = new List<string>();
        var p = e.EnemyPlayer;
        if (p == null)
        {
            why = System.Array.Empty<string>();
            return 0;
        }
        float score = GearWeakness(e, p, parts);
        switch (p.HealthStatus)
        {
            case ETagStatus.Injured:
                score += .05f;
                parts.Add("hurt");
                break;
            case ETagStatus.BadlyInjured:
                score += .2f;
                parts.Add("badly hurt");
                break;
            case ETagStatus.Dying:
                score += .35f;
                parts.Add("dying");
                break;
        }
        if (e.Status.VulnerableAction != EEnemyAction.None)
        {
            score += .15f;
            parts.Add("busy (" + e.Status.VulnerableAction + ")");
        }
        if (Alone(b, e))
        {
            score += .1f;
            parts.Add("alone");
        }
        why = parts.ToArray();
        return Mathf.Clamp01(score);
    }

    internal static float GearWeakness(Enemy enemy, Player p, List<string> parts)
    {
        float score = 0f;
        var equipment = p.Inventory?.Equipment;
        float body = HighestClass(equipment, EquipmentSlot.ArmorVest);
        if (body <= 0f)
        {
            body = HighestClass(equipment, EquipmentSlot.TacticalVest);
        }
        if (body <= 0f)
        {
            score += 0.3f;
            parts.Add("no armor");
        }
        else if (body <= 2f)
        {
            score += 0.2f;
            parts.Add($"armor class {body:0}");
        }
        else if (body <= 3f)
        {
            score += 0.08f;
            parts.Add("armor class 3");
        }
        float helmet = HighestClass(equipment, EquipmentSlot.Headwear);
        if (helmet <= 0f)
        {
            score += 0.15f;
            parts.Add("no helmet");
        }
        else if (helmet <= 2f)
        {
            score += 0.05f;
            parts.Add("weak helmet");
        }
        var weapon = enemy.EnemyPlayerComponent?.Equipment?.CurrentWeaponInfo;
        if (weapon == null || p.HandsController is not Player.FirearmController)
        {
            score += 0.35f;
            parts.Add("no gun out");
        }
        else if (weapon.WeaponClass == EWeaponClass.pistol)
        {
            score += 0.3f;
            parts.Add("pistol");
        }
        else if (weapon.WeaponClass == EWeaponClass.smg)
        {
            score += 0.05f;
            parts.Add("smg");
        }
        return score;
    }

    internal static bool Alone(BotComponent bot, Enemy enemy)
    {
        var known = bot.EnemyController?.KnownEnemies;
        if (known == null)
        {
            return true;
        }
        Vector3 pos = enemy.EnemyPosition;
        foreach (Enemy other in known)
        {
            if (other == null || ReferenceEquals(other, enemy) || !other.EnemyPlayer)
            {
                continue;
            }
            if (
                (other.EnemyPosition - pos).sqrMagnitude < 20f * 20f
                && other.TimeSinceLastKnownUpdated < 20f
            )
            {
                return false;
            }
        }
        return true;
    }

    internal static float HighestClass(InventoryEquipment equipment, EquipmentSlot slot)
    {
        Item item = equipment?.GetSlot(slot)?.ContainedItem;
        if (item == null)
        {
            return 0f;
        }
        _armor.Clear();
        item.GetItemComponentsInChildrenNonAlloc(_armor, true);
        float best = 0f;
        foreach (var a in _armor)
        {
            if (a.ArmorClass > best)
            {
                best = a.ArmorClass;
            }
        }
        _armor.Clear();
        return best;
    }
}
