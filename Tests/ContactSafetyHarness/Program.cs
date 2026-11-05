using Comet.Client;
using SAIN.Components;
using SAIN.SAINComponent.Classes.EnemyClasses;

int n=0;void Check(bool ok,string why){n++;if(!ok)throw new Exception(why);}
var b=new BotComponent{ProfileId="one"};var other=new Owner();
var e=new Enemy{EnemyProfileId="two",EnemyPlayer=new(){AIData=new(){BotOwner=other}}};b.GoalEnemy=e;
Check(ContactSafety.AllowsGoal(b),"unrelated squad accepted, regardless of same faction");
b.Squad.Members.Add("two",new());Check(!ContactSafety.AllowsGoal(b),"SAIN squad identity rejects native GoalEnemy");
b.Squad.Members.Clear();other.BotsGroup=b.BotOwner.BotsGroup;Check(!ContactSafety.Allows(b,e),"native group identity rejects during SAIN setup");
other.BotsGroup=new();b.BotOwner.BotsGroup.Members.Add(other);Check(!ContactSafety.Allows(b,e),"native membership rejects even while group pointer is stale");
b.BotOwner.BotsGroup.Members.Clear();Check(ContactSafety.AllowsGoal(b),"membership read live, no stale cache");
e.EnemyProfileId="one";Check(!ContactSafety.Allows(b,e),"self rejected");
e.EnemyProfileId="";Check(!ContactSafety.Allows(b,e),"missing identity rejected");
e.EnemyProfileId="human";e.EnemyPlayer.AIData.BotOwner=null;Check(ContactSafety.Allows(b,e),"human opponent remains eligible");
Check(!ContactSafety.Allows(b,null),"no contact rejected");
b.GoalEnemy=null;Check(!ContactSafety.AllowsGoal(b),"ended contact rejected");
Console.WriteLine($"Contact safety: {n} production helper checks passed.");

namespace SAIN.Components {
 public class Group {public HashSet<Owner> Members=new();public bool Contains(Owner b)=>Members.Contains(b);}
 public class Owner {public Group BotsGroup=new();}
 public class Squad {public Dictionary<string,object> Members=new();}
 public class BotComponent {public string ProfileId="";public Squad Squad=new();public Owner BotOwner=new();public Enemy? GoalEnemy;}
}
namespace SAIN.SAINComponent.Classes.EnemyClasses {
 public class Data {public Owner? BotOwner;}
 public class Player {public Data AIData=new();}
 public class Enemy {public string EnemyProfileId="";public Player? EnemyPlayer;}
}
