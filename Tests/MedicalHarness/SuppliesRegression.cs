using Comet.Core;
using EFT;
using EFT.InventoryLogic;
using SAIN.Components;

namespace Comet.Client;

public sealed partial class Plugin
{
    internal MedicalFrame Supplies(BotComponent bot)
    {
        if (!states.TryGetValue(bot, out var state))
            states[bot] = state = new BotState(bot, config!, session);
        var frame = new MedicalFrame();
        state.Medical.Items.Clear();
        MedicalSupplies(bot, state, frame, state.Medical.Chosen);
        return frame;
    }

    internal static int SuppliesRegression()
    {
        int count = 0;
        for (int size = 1; size <= 32; size++)
        {
            var plugin = new Plugin();
            var bot = new BotComponent();
            bot.Player.HealthController.Parts[EBodyPart.Chest].Current = 20;
            for (int i = 0; i < size; i++)
            {
                var med = new MedKit { Id = "kit" + i };
                med.HealthEffectsComponent.Duration = 32 - i;
                bot.Player.InventoryController.Items.Add(med);
            }
            var frame = plugin.Supplies(bot);
            var option = frame.Options.Single(x => x.Action == "FirstAid");
            if (
                option.Item != "kit" + (size - 1)
                || option.Part != "Chest"
                || option.Need != "vital"
            )
                throw new Exception("rank and fastest-kit tie break changed");
            count++;
            if (
                bot.Player.HealthController.HealthReads != 7
                || bot.Player.HealthController.EffectReads != 14
            )
                throw new Exception("health reads must not scale with inventory size");
            count++;
            var old = option;
            bot.Player.HealthController.Parts[EBodyPart.Chest].Current = 100;
            bot.Player.HealthController.Parts[EBodyPart.LeftArm].Current = 10;
            var next = plugin.Supplies(bot).Options.Single(x => x.Action == "FirstAid");
            if (next.Part != "LeftArm" || old.Part != "Chest")
                throw new Exception("body snapshot leaked across calls or old diagnostic mutated");
            count++;
        }
        return count;
    }
}
