using Comet.Client;
using EFT;
using EFT.Ballistics;
using EFT.Interactive;
using SAIN.Classes.Bot.Sense.Hearing;
using SAIN.Components;
using SAIN.Components.BotController;
using SAIN.Components.BotControllerSpace.Classes;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.Decision;
using SAIN.SAINComponent.SubComponents.CoverFinder;
using UnityEngine;
using UnityEngine.AI;

return Plugin.RunChecks();

namespace Comet.Client
{
    public sealed partial class Plugin
    {
        internal static int RunChecks()
        {
            var p = new Plugin();
            Instance = p;
            int checks = 0;
            void Check(bool value, string message)
            {
                checks++;
                if (!value) throw new Exception(message);
            }

            var manager = new BotManagerComponent();
            var bot = new BotComponent();
            var unsupported = new BotComponent { Supported=false };
            BotManagerComponent.Instance = manager;
            manager.Bots.Add(1,bot);
            manager.Bots.Add(2,unsupported);
            p.world = manager.GameWorld;
            p.config = null;
            RemixBotSpawned(bot,true);
            Check(bot.Talk.EnemyTalk.Applied == 0,"spawn waits for raid configuration");
            p.config = new();
            p.Authority = false;
            p.ConfigureExistingRemixBots();
            Check(bot.Grenade.GrenadeThrowDecider.Applied == 0,"Fika client keeps native settings");
            p.Authority = true;
            p.ConfigureExistingRemixBots();
            RemixBotSpawned(bot,true);
            Check(bot.Talk.EnemyTalk.Applied == 1 && bot.Grenade.GrenadeThrowDecider.Applied == 1,"late config applies both presets exactly once");
            var later = new BotComponent();
            RemixBotSpawned(later,false);
            Check(later.Talk.EnemyTalk.Applied == 0,"failed initialization skipped");
            RemixBotSpawned(later,true);
            Check(later.Grenade.GrenadeThrowDecider.Applied == 1 && unsupported.Talk.EnemyTalk.Applied == 0,"new supported spawn only");
            bool respond=true;
            RemixTalkUpdated(bot.Talk.EnemyTalk,SAIN.SAINPlugin.LoadedPreset,ref respond);
            Check(!respond && bot.Talk.GroupTalk.Applied == 1,"taunts off also mutes replies and refreshes squad talk");
            bot.Info.FileSettings.Mind.BotTaunts=true;
            respond=true;
            RemixTalkUpdated(bot.Talk.EnemyTalk,SAIN.SAINPlugin.LoadedPreset,ref respond);
            Check(respond && bot.Talk.GroupTalk.Applied == 2,"preset reload restores enabled responses");
            float arc=.66f;
            RemixGrenadeUpdated(unsupported.Grenade.GrenadeThrowDecider,ref arc);
            Check(arc==.66f,"unsupported native arc");
            RemixGrenadeUpdated(bot.Grenade.GrenadeThrowDecider,ref arc);
            Check(arc==0,"reload preserves fork grenade trajectory cutoff");

            var decision = new SquadDecisionClass(bot);
            var leader = new BotComponent();
            bool result = true;
            bool Follows()
            {
                result=true;
                Check(!RemixGroupSearch(decision,ref result),"authority decides group search");
                return result;
            }
            Check(!Follows(),"leader absent");
            bot.GoalEnemy = new();
            bot.Squad.SquadInfo.LeaderComponent=bot;
            bot.Decision.CurrentCombatDecision=ECombatDecision.Search;
            Check(!Follows(),"leader cannot follow itself");
            bot.Squad.SquadInfo.LeaderComponent=leader;
            leader.GoalEnemy=bot.GoalEnemy;
            Check(!Follows(),"searching follower cannot recruit squad");
            leader.Decision.CurrentCombatDecision=ECombatDecision.Search;
            Check(Follows(),"follow searching leader with shared target");
            leader.GoalEnemy=new();
            Check(!Follows(),"different target");
            p.Authority=false;
            Check(RemixGroupSearch(decision,ref result),"Fika peer defers");
            p.Authority=true;

            float timer=0;
            bool Wound() => RemixWoundRoll(bot.Talk.EnemyTalk,ref result,true,2,ref timer);
            bot.Memory.Health.HealthStatus=ETagStatus.BadlyInjured;
            bot.Talk.CanTalk=false;
            Check(!Wound() && MyExtensions.Rolls==0 && !RemixGrenadeScream(bot.Talk.EnemyTalk),"muted fake screams consume no roll");
            bot.Talk.CanTalk=true;
            Check(RemixGrenadeScream(bot.Talk.EnemyTalk),"grenade scream uses native path when enabled");
            bot.Squad.BotInGroup=true;
            Wound();
            bot.Squad.BotInGroup=false;
            bot.GoalEnemy.EnemyPosition=new(70,0,0);
            Wound();
            Check(MyExtensions.Rolls==0,"squad and exact distance boundary");
            bot.GoalEnemy.EnemyPosition=new(69,0,0);
            Wound(); Wound();
            Check(MyExtensions.Rolls==1 && bot.Talk.Said==0,"failed roll not retried on the same wound");
            bot.Memory.Health.HealthStatus=ETagStatus.Healthy;
            Wound();
            bot.Memory.Health.HealthStatus=ETagStatus.Dying;
            MyExtensions.Result=true;
            Wound();
            Check(result && bot.Talk.Said==1 && timer==Time.time+30,"new wound gets a fresh roll and native cooldown");
            Time.time+=31;
            Wound();
            Check(MyExtensions.Rolls==2,"cooldown expiry alone does not rearm a wound");

            var data=bot.Transform.NavData;
            var physical=new Vector3(20,9,5);
            var wrongHeight=new Vector3(20,13,5);
            void Sample(float xOffset)
            {
                data.LastValidHit = new NavMeshHit { position=new(0,9,5) };
                StoreRemixNavSample(data,new(xOffset,9,5));
            }
            Sample(.35f);
            Check(RemixDataOrigin(data)==data.Position,"no transition at .35 m");
            Sample(.36f);
            Check(RemixBotOrigin(bot)==data.LastValidHit.position && data.Position.x==.36f,"sampled start independent of physical position");
            Sample(.30f);
            Check(RemixBotOrigin(bot)==data.LastValidHit.position && p.counts[NativeFix.OffMeshStart]==1,"hysteresis and transition-only diagnostic");
            p.Authority=false;
            Check(RemixMovementOrigin(bot,physical)==physical && RemixDataOrigin(data)==data.Position,"inactive authority keeps native origin");
            p.Authority=true;
            Sample(.25f);
            Check(RemixBotOrigin(bot)==data.Position,"back on at .25 m");
            Check(RemixMovementOrigin(bot,wrongHeight,preserveOnMesh:true)==wrongHeight,"COMET's original start on valid ground");
            data.LastValidHit=new() { position=new(0,100,0) };
            StoreRemixNavSample(data,new(.2f,2,.2f));
            Check(RemixDataOrigin(data)==data.Position,"height alone does not put a bot off the mesh");
            StoreRemixNavSample(data,new(.26f,2,.26f));
            Check(RemixDataOrigin(data)==data.LastValidHit.position,"diagonal planar displacement counts");
            var alreadyOff=new BotComponent();
            alreadyOff.Transform.NavData.Position=new(1,2,0);
            alreadyOff.Transform.NavData.LastValidHit=new() { position=new(0,2,0) };
            RemixBotSpawned(alreadyOff,true);
            Check(RemixBotOrigin(alreadyOff)==new Vector3(0,2,0),"existing successful sample available immediately at registration");
            var analyzer=new CoverAnalyzer(bot);
            Sample(.5f);
            var coverStart=physical;
            RemixCoverPathOrigin(analyzer,ref coverStart);
            Check(coverStart==data.LastValidHit.position,"same sampled start for new and retained cover");
            Check(RemixMovementOrigin(unsupported,physical)==physical,"unregistered actor keeps position");

            var root=new Transform();
            var leaf=root.AddChild();
            leaf.Door=new Door(leaf);
            var leafCollider=new Collider(leaf.AddChild(),1);
            var frame=new Collider(root.AddChild(),2) { bounds=new Bounds { center=new(1.49f,0,0) } };
            var wall=new Collider(root.AddChild(),3) { bounds=new Bounds { center=new(1.5f,0,0) } };
            bool Rejected(Collider collider, BotComponent actor)
            {
                bool accepted=true;
                string reason="";
                var hit=new NavMeshHit {position=physical};
                bool run=RemixDoorCover(new CoverAnalyzer(actor),collider,ref reason,ref hit,ref accepted);
                return !run && !accepted && reason=="door-leaf-or-frame" && hit.position==default;
            }
            Check(Rejected(leafCollider,bot) && Rejected(frame,bot),"leaf descendant and sibling frame");
            Check(!Rejected(wall,bot) && !Rejected(new Collider(new Transform(),4),bot),"boundary, ordinary wall and parentless collider retained");
            Check(!Rejected(frame,unsupported),"unsupported bot uses native cover");
            Check(!Rejected(new Collider(new Transform(),1),unsupported),"replica decision independent of cached collider");
            var oldFrame=new Point(frame);
            var solidWall=new Point(wall);
            later.Cover.CoverPoints.AddRange(new[]{oldFrame,solidWall});
            later.Cover.CoverInUse=oldFrame;
            later.Cover.CoverPoint_MovingTo=solidWall;
            p.RemoveCachedDoorCover(later);
            Check(later.Cover.CoverPoints.SequenceEqual(new[]{solidWall}) && later.Cover.CoverInUse==null &&
                ReferenceEquals(later.Cover.CoverPoint_MovingTo,solidWall),"pre-config door cover released without cancelling a valid wall route");
            p.ClearRemixRaidState();
            Check(!Rejected(new Collider(new Transform(),1),bot) && RemixDataOrigin(data)==data.Position,"next raid reuses IDs without borrowing old results");

            var listener=new HearingInput(bot);
            var talk=bot.Talk.EnemyTalk;
            int impacts=0, phrases=0;
            Action<Shot> onImpact=_=>impacts++;
            Action<EPhraseTrigger,ETagStatus,Player> onTalk=(_,_,_)=>phrases++;
            manager.BotHearing.BulletImpact+=onImpact;
            manager.BotHearing.PlayerTalk+=onTalk;
            RememberHearingSource(listener);
            RememberHearingSource(talk);
            var next=new BotManagerComponent();
            next.BotHearing.BulletImpact+=onImpact;
            next.BotHearing.PlayerTalk+=onTalk;
            RemoveBulletListener(HearingForDisposal(next,listener),onImpact);
            RemoveTalkListener(HearingForDisposal(null,talk),onTalk);
            RemoveBulletListener(HearingForDisposal(next,listener),onImpact);
            manager.BotHearing.Emit();
            next.BotHearing.Emit();
            Check(impacts==1 && phrases==1,"late and duplicate teardown detach only original hearing publisher");
            p.world=null;
            BotManagerComponent.Instance=next;
            BotSpawnController.Instance=new(manager);
            SAINWeatherClass.Instance=new(next);
            RemixManagerDisposed(manager);
            Check(ReferenceEquals(BotManagerComponent.Instance,next) && BotSpawnController.Instance==null &&
                ReferenceEquals(SAINWeatherClass.Instance!.BotController,next),"old manager releases only its own statics after the world has ended");
            p.world=next.GameWorld;
            RemixManagerStarted(next);
            BotSpawnController.Instance=new(next);
            var lifetime=p.remixManagers.GetValue(next,_=>throw new Exception("missing lifetime hook"));
            typeof(RemixManagerLifetime).GetMethod("OnDestroy",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(lifetime,null);
            RemixManagerDisposed(next);
            Check(BotManagerComponent.Instance==null && BotSpawnController.Instance==null && SAINWeatherClass.Instance==null,"normal disposal releases all three owner references");
            Console.WriteLine($"{checks} Remix regression checks passed against production callbacks.");
            return 0;
        }
    }
}
