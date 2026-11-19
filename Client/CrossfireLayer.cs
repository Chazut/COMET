using DrakiaXYZ.BigBrain.Brains;
using EFT;
using SAIN.Layers;
using SAIN.Models.Enums;

namespace Comet.Client;

public sealed class CrossfireLayer : SAINLayer
{
    public CrossfireLayer(BotOwner owner, int priority)
        : base(owner, priority, "COMET : Crossfire", ESAINLayer.Squad) { }

    public override bool IsActive()
    {
        bool active = GetBotComponent() && Plugin.CrossfireActive(BotOwner);
        CheckActiveChanged(active);
        return active;
    }

    public override Action GetNextAction() =>
        new(typeof(CrossfireAction), "Take a different firing angle");

    public override bool IsCurrentActionEnding() => false;
}

public sealed class CrossfireAction : BotAction
{
    public CrossfireAction(BotOwner owner)
        : base(owner, nameof(CrossfireAction)) { }

    public override void Start()
    {
        base.Start();
        Plugin.RunCrossfire(BotOwner, true);
    }

    public override void Update(CustomLayer.ActionData data) => Plugin.RunCrossfire(BotOwner, true);

    public override void Stop()
    {
        Plugin.RunCrossfire(BotOwner, false);
        base.Stop();
    }

    public override void OnSteeringTicked()
    {
        if (TacticalSteeringFire.TryShoot(Bot, TacticalSteeringKind.SquadAngle))
            return;
        if (!Plugin.SteerCrossfire(BotOwner))
            Bot.Steering.SteerByPriority(Bot.GoalEnemy);
    }
}
