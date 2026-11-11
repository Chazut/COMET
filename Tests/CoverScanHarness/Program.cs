using System.Collections;
using System.Reflection.Emit;
using Comet.Client;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using SAIN.Components;
using SAIN.Components.CoverFinder;
using SAIN.SAINComponent.SubComponents.CoverFinder;
using UnityEngine;

int checks=0;void Check(bool b,string why){checks++;if(!b)throw new Exception("C07 adapter: "+why);}
void Tick(IEnumerator e,double at){Time.timeAsDouble=at;Time.frameCount++;Check(e.MoveNext(),"iterator alive");}
var plugin=new Plugin();plugin.Patches();
var getter=AccessTools.PropertyGetter(typeof(CoverFinderComponent),"CoverPoints");
var il=Enumerable.Range(0,6).Select(_=>new CodeInstruction(OpCodes.Callvirt,getter)).ToArray();
var patched=Plugin.RedirectCoverScan(il).ToArray();Check(patched.Length==6&&patched.All(i=>i.opcode==OpCodes.Call&&i.operand.ToString()!.Contains("ScanCoverPoints")),"only six list calls replaced");
bool rejected=false;try{Plugin.RedirectCoverScan(new[]{new CodeInstruction(OpCodes.Callvirt,getter)}).ToArray();}catch(InvalidOperationException){rejected=true;}Check(rejected,"fail closed for incompatible iterator");
var b=new BotComponent();var f=new CoverFinderComponent(b);var old=new CoverPoint{Id="old"};f.CoverPoints.Add(old);var e=plugin.Wrap(f);
Tick(e,0);Check(f.CoverPoints.SequenceEqual(new[]{old})&&Plugin.ScanCoverPoints(f)==f.CoverPoints,"other readers never see pending list");
Tick(e,4);Check(f.CoverPoints[0]==old,"past 3s retention");Check(Plugin.RetainedCover(b,old),"COMET selection can still consider retained results");
Tick(e,9);Tick(e,9.1);Check(f.CoverPoints.Count==3&&!f.CoverPoints.Contains(old),"publish only after completion");
Tick(e,10);Tick(e,20);Check(f.CoverPoints.Count==3&&JObject.Parse(Comet.Api.Decisions.Events[^1])["coverScan"]!["state"]!.ToString()=="timeout","watchdog retains complete list");
Tick(e,20.2);b.GoalEnemy!.EnemyProfileId="new-contact";plugin.Sweep();Check(f.CoverPoints.Count==0&&!Plugin.RetainedCover(b,old),"enemy invalidation outside coroutine");
Tick(e,21);Tick(e,22);Tick(e,23);Tick(e,24);
var selected=f.CoverPoints[0];Check(plugin.Validate(b,selected)&&f.Analyzer.Calls==1,"validate before mover");
Check(plugin.Validate(b,selected)&&f.Analyzer.Calls==1,"same-frame repeated movement request shares check");
Check(Plugin.SelectionCoverUsable(b,selected,out var why)&&f.Analyzer.Calls==1,"selection and mover share same-frame geometry check");
b.NavMeshPosition=new Vector3(1,0,0);Check(plugin.Validate(b,selected)&&f.Analyzer.Calls==2,"same-frame moved bot invalidates geometry cache");
b.GoalEnemy!.LastKnownPosition=new Vector3(11,0,0);Check(plugin.Validate(b,selected)&&f.Analyzer.Calls==3,"same-frame updated known target invalidates geometry cache");
b.GoalEnemy.EnemyProfileId="different";Check(plugin.Validate(b,selected)&&f.Analyzer.Calls==4,"same frame new enemy rechecked");
selected.Pass=false;Time.frameCount++;Check(!plugin.Validate(b,selected)&&selected.CoverData.IsBad,"blocked cover refused");
Check(!Plugin.SelectionCoverUsable(b,selected,out why)&&why=="cover-selected-invalid-path-blocked","cached refusal preserves exact geometry reason");
selected.Pass=true;Time.frameCount++;Check(plugin.Validate(b,selected),"fresh valid cover");selected.Spotted=true;
Check(!plugin.Validate(b,selected),"same-frame spotted cover invalidates previously safe selection");
selected.Pass=true;selected.Spotted=true;Time.frameCount++;Check(!plugin.Validate(b,selected),"spotted cover refused");
selected.Spotted=false;selected.Collider=null;Time.frameCount++;Check(!plugin.Validate(b,selected),"destroyed collider refused");
plugin.IsGhost=true;plugin.Sweep();int calls=f.Iterations;Tick(e,25);Check(calls==f.Iterations&&f.CoverPoints.Count==0,"ghost no scan or retained cover");
plugin.IsGhost=false;Tick(e,26);plugin.Stop(f);Check(plugin.Count==0&&!e.MoveNext(),"StopLooking releases iterator and references");
foreach(var variant in new[]{"scav","player","replica","disabled","inactive","dead","ending"}){
 plugin=new();b=new();f=new(b);if(variant is "scav" or "player")b.Pmc=false;if(variant=="replica")plugin.Authority=false;if(variant=="disabled")plugin.config.CoverScan.Enabled=false;if(variant=="inactive")b.BotActive=false;if(variant=="dead")b.IsDead=true;if(variant=="ending")b.GameEnding=true;
 e=plugin.Wrap(f);Tick(e,30);Check(f.CoverPoints.Count==(variant is "inactive" or "ending"?0:1),variant+" follows expected native/suspended scope");plugin.End();Check(plugin.Count==0,"raid cleanup");
}
plugin=new();b=new();f=new(b){Steps=0};f.CoverPoints.Add(new());e=plugin.Wrap(f);Tick(e,40);Check(f.CoverPoints.Count==0,"real empty publishes empty");
f.Steps=2;Tick(e,41);f.ChangeContact=true;Tick(e,42);Check(f.CoverPoints.Count==0,"mixed target scan discarded");f.ChangeContact=false;Tick(e,43);f.Throw=true;Tick(e,44);Check(plugin.Warnings==1&&Plugin.ScanCoverPoints(f)==f.CoverPoints,"exception returns list routing to native");f.Throw=false;Tick(e,45);Check(f.CoverPoints.Count==1,"native fallback after adapter error");plugin.End();
// Production serialization used unchanged by RR fixture, parser, archive and preview.
var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../artifacts/c07"));Directory.CreateDirectory(root);
File.WriteAllLines(Path.Combine(root,"cover-scan.jsonl"),Comet.Api.Decisions.Events);
var fixture=Comet.Api.Decisions.Events.First(j=>JObject.Parse(j)["coverScan"]!["state"]!.ToString()=="scanning"&&JObject.Parse(j)["coverScan"]!["elapsed"]!.Value<double>()==4);
File.WriteAllText(Path.Combine(root,"cover-scan.json"),fixture);
Console.WriteLine($"C07 production adapter: {checks} checks passed, {Comet.Api.Decisions.Events.Count} serialized events. Engine calls doubled; no Unity raid.");
