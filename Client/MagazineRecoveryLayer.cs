using DrakiaXYZ.BigBrain.Brains;
using EFT;
using SAIN.Layers;
using SAIN.Models.Enums;

namespace Comet.Client;

// Below post-combat medical recovery (21), above ORBIT travel/looting (19).
public sealed class MagazineRecoveryLayer : SAINLayer
{
    public MagazineRecoveryLayer(BotOwner owner,int priority)
        :base(owner,priority,"COMET : Recover magazine",ESAINLayer.Squad) { }
    public override bool IsActive()
    {
        bool active=GetBotComponent()&&Plugin.MagazineRecoveryActive(BotOwner);
        CheckActiveChanged(active);
        return active;
    }
    public override Action GetNextAction()=>new(typeof(MagazineRecoveryAction),"Recover discarded magazine");
    public override bool IsCurrentActionEnding()=>false;
}

public sealed class MagazineRecoveryAction : BotAction
{
    public MagazineRecoveryAction(BotOwner owner):base(owner,nameof(MagazineRecoveryAction)) { }
    public override void Start(){base.Start();Plugin.RunMagazineRecovery(BotOwner,true);}
    public override void Update(CustomLayer.ActionData data)=>Plugin.RunMagazineRecovery(BotOwner,true);
    public override void Stop(){Plugin.RunMagazineRecovery(BotOwner,false);base.Stop();}
    public override void OnSteeringTicked()
    {
        if(TacticalSteeringFire.TryShoot(Bot,TacticalSteeringKind.PostCombat))return;
        Bot.Steering.SteerByPriority(Bot.GoalEnemy);
    }
}
