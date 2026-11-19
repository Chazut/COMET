using Comet.Client;
using EFT;
using EFT.Ballistics;
using EFT.InventoryLogic;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;

internal static class H02Tests
{
    internal static void Run()
    {
        int n = 0;
        void C(bool v, string m)
        {
            n++;
            if (!v)
                throw new Exception("H02 source: " + m);
        }
        (Plugin p, BotComponent b) Setup()
        {
            Time.time = 10;
            var p = new Plugin();
            p.config!.SurgeryInterruption.Enabled = true;
            var b = new BotComponent();
            b.EnemyController.KnownEnemies.Add(new(b));
            b.Player.HealthController.Parts[EBodyPart.LeftLeg].Current = 0;
            b.Player.HealthController.Destroyed.Add(EBodyPart.LeftLeg);
            var item = new Meds { Id = "cms", AllowedPart = EBodyPart.LeftLeg };
            item.HealthEffectsComponent.Duration = 20;
            item.HealthEffectsComponent.DamageEffects[EDamageEffectType.DestroyedPart] = new();
            b.Player.InventoryController.Items.Add(item);
            C(p.Choose(b).decision == ESelfActionType.Surgery, "source relaxed surgery accepted");
            p.Start(b);
            return (p, b);
        }
        foreach (var reason in new[] { "hit", "visible", "close", "native" })
        {
            var (p, b) = Setup();
            var kit = b.BotOwner.Medecine.SurgicalKit;
            var e = b.EnemyController.KnownEnemies[0];
            Time.time = 11;
            switch (reason)
            {
                case "hit":
                    b.Medical.TimeSinceShot = 0;
                    break;
                case "visible":
                    e.IsVisible = true;
                    break;
                case "close":
                    e.Path.PathLength = 19;
                    break;
                default:
                    kit.CancelCurrent();
                    break;
            }
            p.Sample(b);
            C(kit.Cancels == 1, reason + " native cancellation");
            p.Sample(b);
            C(kit.Cancels == 1, "no duplicate cancellation");
            C(p.states[b].Medical.Interruption.RetryAt == 0, "no custom retry ban");
            C(
                b.Player.HealthController.IsBodyPartDestroyed(EBodyPart.LeftLeg),
                "no synthetic repair"
            );
            p.End(b);
            C(!p.states[b].Medical.Interruption.Active, "cleanup");
        }
        foreach (var mode in new[] { "grenade", "no-cover", "known" })
        {
            var (p, b) = Setup();
            Time.time = 11;
            if (mode == "grenade")
                b.Grenade.GrenadeDangerPoint = new();
            if (mode == "no-cover")
                b.Cover.CoverInUse = null;
            if (mode == "known")
                b.EnemyController
                    .KnownEnemies[0]
                    .KnownPlaces
                    .LastSeenPlace!
                    .TimeSincePositionUpdated = 0;
            p.Sample(b);
            C(b.BotOwner.Medecine.SurgicalKit.Cancels == 0, "no additional ongoing veto: " + mode);
        }
        foreach (var mode in new[] { "ghost", "authority", "off" })
        {
            var (p, b) = Setup();
            if (mode == "ghost")
                p.IsGhost = true;
            if (mode == "authority")
                p.Authority = false;
            if (mode == "off")
                p.config!.SurgeryInterruption.Enabled = false;
            b.Medical.TimeSinceShot = 0;
            p.Sample(b);
            C(b.BotOwner.Medecine.SurgicalKit.Cancels == 0, "scope remains isolated: " + mode);
        }
        Console.WriteLine($"H02 source adapter: {n} checks passed");
    }
}
