using DrakiaXYZ.BigBrain.Brains;
using EFT;
using SAIN.Layers;
using SAIN.Models.Enums;
using UnityEngine;

namespace Comet.Client;

// Above ORBIT (19), below SAIN squad priorities; never active while a native combat decision exists.
public sealed class PostCombatLayer : SAINLayer
{
    public PostCombatLayer(BotOwner owner, int priority)
        : base(owner, priority, "COMET : Post-combat", ESAINLayer.Squad) { }

    public override bool IsActive()
    {
        bool active = GetBotComponent() && Plugin.PostCombatActive(BotOwner);
        CheckActiveChanged(active);
        return active;
    }

    public override Action GetNextAction() => new(typeof(PostCombatAction), "Post-combat recovery");

    public override bool IsCurrentActionEnding() => false;
}

public sealed class PostCombatAction : BotAction
{
    private float nextGlance;
    private Vector3 glanceOffset;

    public PostCombatAction(BotOwner owner)
        : base(owner, nameof(PostCombatAction)) { }

    public override void Start()
    {
        base.Start();
        Plugin.RunPostCombat(BotOwner, true);
    }

    public override void Update(CustomLayer.ActionData data) =>
        Plugin.RunPostCombat(BotOwner, true);

    public override void Stop()
    {
        Plugin.RunPostCombat(BotOwner, false);
        base.Stop();
    }

    public override void OnSteeringTicked()
    {
        if (!Plugin.SteerPostCombat(BotOwner, this))
            Bot.Steering.SteerByPriority(Bot.GoalEnemy);
    }

    internal Vector3 GetGlanceOffset(Vector3 position, Vector3 threat)
    {
        if (nextGlance < Time.time)
        {
            nextGlance = Time.time + UnityEngine.Random.Range(1.5f, 3f);
            var direction = threat - position;
            direction.y = 0;
            glanceOffset = UnityEngine.Random.value < .5f
                ? Vector3.zero
                : Vector3.Cross(Vector3.up, direction.normalized)
                    * UnityEngine.Random.Range(-.5f, .5f) * direction.magnitude;
        }
        return glanceOffset;
    }
}
