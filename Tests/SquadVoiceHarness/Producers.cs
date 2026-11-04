using System.Runtime.CompilerServices;
namespace SAIN.SAINComponent.Classes.Talk;
public partial class GroupTalk {
[MethodImpl(MethodImplOptions.NoInlining)] public void OnDecisionMade()=>Request?.Invoke();
[MethodImpl(MethodImplOptions.NoInlining)] public void OnMemberMadeDecision()=>Request?.Invoke();
[MethodImpl(MethodImplOptions.NoInlining)] public void TalkSelfDecision()=>Request?.Invoke();
[MethodImpl(MethodImplOptions.NoInlining)] public void TalkSoloDecision()=>Request?.Invoke();
[MethodImpl(MethodImplOptions.NoInlining)] public void LeaderMadeDecision()=>Request?.Invoke();
[MethodImpl(MethodImplOptions.NoInlining)] public void ShallReportLostVisual()=>Request?.Invoke();
[MethodImpl(MethodImplOptions.NoInlining)] public void EnemyConversation()=>Request?.Invoke();
[MethodImpl(MethodImplOptions.NoInlining)] public void TalkEnemySniper()=>Request?.Invoke();
[MethodImpl(MethodImplOptions.NoInlining)] public void onEnemyHealthChanged()=>Request?.Invoke();
[MethodImpl(MethodImplOptions.NoInlining)] public void CheckEnemyContact()=>Request?.Invoke();
[MethodImpl(MethodImplOptions.NoInlining)] public void OnEnemyDown()=>Request?.Invoke();
[MethodImpl(MethodImplOptions.NoInlining)] public void friendlyDown()=>Request?.Invoke();
[MethodImpl(MethodImplOptions.NoInlining)] public void OnLootBody()=>Request?.Invoke();
[MethodImpl(MethodImplOptions.NoInlining)] public void allMembersSay()=>Request?.Invoke();
[MethodImpl(MethodImplOptions.NoInlining)] public void UpdateLeaderCommand()=>Request?.Invoke();
[MethodImpl(MethodImplOptions.NoInlining)] public void myHealthChanged()=>Request?.Invoke();
[MethodImpl(MethodImplOptions.NoInlining)] public void ShallReportNeedHelp()=>Request?.Invoke();
[MethodImpl(MethodImplOptions.NoInlining)] public void enemyHeard()=>Request?.Invoke();
[MethodImpl(MethodImplOptions.NoInlining)] public void CheckIfLeaderShouldCommand()=>Request?.Invoke();
[MethodImpl(MethodImplOptions.NoInlining)] public void checkLeaderTalk()=>Request?.Invoke();
[MethodImpl(MethodImplOptions.NoInlining)] public void TalkEnemyLocation()=>Request?.Invoke();
}