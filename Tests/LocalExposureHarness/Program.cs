using System.Text.Json;
using Comet.Client;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;

int checks = 0;
void Check(bool pass, string why)
{
    if (!pass) throw new Exception(why);
    checks++;
}
void Near(float actual, float expected, string why) => Check(Math.Abs(actual - expected) < 0.0001f, why);
void Advance(float seconds)
{
    Time.realtimeSinceStartup = Time.time += seconds;
    Time.frameCount++;
}

Time.time = Time.realtimeSinceStartup = 100f;
var p = Plugin.Instance = new Plugin();
var local = new Enemy();
local.EnemyPlayer.IsYourPlayer = true;
var guest = new Enemy();
var fixture = new Light { intensity = 2f, range = 6f };
fixture.transform.position = new Vector3(20, 2, 0);
UnityEngine.Object.Scene = [fixture];
guest.EnemyTransform.EyePosition = new Vector3(20, 1, 0);
TOD_Sky.Instance = new() { SunDirection = Vector3.up };
Physics.Roof = true;
p.Update();

foreach (float preset in new[] { 0.3f, 1f, 4f })
    Near(Plugin.Sight(local, preset), preset * 1.28f, "preset retained in shade");
Check(p.Samples == 1, "all observers of the same player share a light sample");
p.fikaPresent = true;
Near(Plugin.Sight(guest, 2), 2, "guest under a fixture on the host");
Check(p.Samples == 2, "guest has its own sample");
var otherBot = new Enemy { EnemyPlayer = guest.EnemyPlayer, EnemyTransform = guest.EnemyTransform };
Near(Plugin.Sight(otherBot, 2), 2, "second observer sees same fixture");
Check(p.Samples == 2, "second bot reused guest's sample");
guest.EnemyTransform.EyePosition = Vector3.up;
Near(Plugin.Sight(guest, 2), 2.56f, "guest moving out of light invalidates cached position");

local.Seen = true;
local.Vision.TimeLastSeen = Time.time - 10;
Near(Plugin.Sight(local, 1), 1, "last confirmed visual contact at ten seconds");
var stranger = new Enemy { EnemyPlayer = local.EnemyPlayer };
Near(Plugin.Sight(stranger, 1), 1.28f, "no contact inheritance between bots");
Advance(0.1f);
Near(Plugin.Sight(local, 1), 1.28f, "contact expires without refreshing itself on evaluation");
local.IsVisible = true;
Near(Plugin.Sight(local, 1), 1, "visible target stays native");
local.IsVisible = local.Seen = false;
local.EnemyInfo.LastGetHitTime = Time.time - 8;
Near(Plugin.Sight(local, 1), 1, "received hit at eight seconds");
local.EnemyInfo.LastGetHitTime = -10;
local.EnemyInfo.LastDoHitTime = Time.time;
Near(Plugin.Sight(local, 1), 1, "dealt hit");
Advance(8.1f);
Near(Plugin.Sight(local, 1), 1.28f, "hit expires");
Near(Plugin.Sight(local, float.MaxValue), float.MaxValue, "extreme preset remains finite");
local.Bot.BotOwner.NightVision.UsingNow = true;
Near(Plugin.Sight(local, 1), 1, "NVG removes extra delay");
local.Bot.BotOwner.NightVision.UsingNow = false;
local.EnemyPlayerComponent.Flashlight.WhiteLight = true;
Near(Plugin.Sight(local, 1), 1, "white light uses SAIN response");
local.EnemyPlayerComponent.Flashlight.WhiteLight = false;
local.RealDistance = 6;
Near(Plugin.Sight(local, 1), 1, "no point-blank shadow");
local.RealDistance = 18;
Near(Plugin.Sight(local, 1), 1.14f, "shadow ramps with distance");
local.RealDistance = 50;
local.IsAI = true;
Near(Plugin.Sight(local, 1), 1.28f, "same SAIN shadow response for an AI target");
local.IsAI = false;
local.Bot.Supported = false;
Near(Plugin.Sight(local, 1), 1, "unsupported observer unchanged");
local.Bot.Supported = true;
p.Authority = false;
Advance(1);
int before = p.Samples;
Near(Plugin.Sight(guest, 1), 1, "Fika replica uses no local modifier");
Check(before == p.Samples, "no replica sampling");
p.Authority = true;
Physics.Roof = false;
Near(Plugin.Sight(guest, 1), 1, "daylight reaches the guest");
p.End();
var summary = p.diagnostics.Lines.Select(s => JsonDocument.Parse(s).RootElement)
    .Last(d => d.GetProperty("stage").GetString() == "summary");
Check(summary.GetProperty("remoteChecks").GetInt32() > 0 && summary.GetProperty("recentSight").GetInt32() > 0 &&
    summary.GetProperty("shadow").GetInt32() > 0, "raid-end summary includes remote contacts and outcomes");
Near(Plugin.Sight(guest, 1), 1, "raid cleanup drops previous samples");

// Exercise scene inputs without Unity rendering or an active player camera.
float Scene(params Light[] lights)
{
    UnityEngine.Object.Scene = lights;
    var sampler = new SceneExposure();
    sampler.RefreshFixtures(Time.time);
    return sampler.Read(local.EnemyPlayer, Vector3.up, Time.time, false);
}
TOD_Sky.Instance = null;
Check(Scene() < 0, "missing signals defer to native");
Near(ExposureResponse.Multiplier(float.NaN, 50), 1, "invalid exposure defers to native");
LightmapSettings.lightProbes = new LightProbes();
LightProbes.Intensity = 0.4f;
Near(Scene(), .4f, "baked illumination");
LightmapSettings.lightProbes = null;
fixture.transform.position = new Vector3(1, 1, 0);
Physics.Wall = true;
Near(Scene(fixture), 0, "light behind a wall gives no illumination");
Physics.Wall = false;
fixture.type = LightType.Spot;
fixture.transform.forward = Vector3.right;
Near(Scene(fixture), 0, "spotlight points away");
fixture.transform.forward = Vector3.left;
Check(Scene(fixture) >= .6f, "spotlight cone illuminates target");
fixture.enabled = false;
fixture.Owner = new CullingLightObject { CurrentLightIntensity = 2 };
Check(Scene(fixture) >= .6f, "Fika guest's fixture survives distance culling");
((CullingLightObject)fixture.Owner).IsLightEnabled = false;
Near(Scene(fixture), 0, "switched-off fixture stays off");
fixture.Parent = local.EnemyPlayer;
Check(Scene(fixture) < 0, "held devices excluded from scene lamps");
fixture.Parent = new Camera();
Check(Scene(fixture) < 0, "render-camera lights excluded");
fixture.Parent = null;
fixture.Owner = null;
fixture.enabled = true;
Physics.Wall = true;
Physics.Lines = 0;
Check(Scene(Enumerable.Repeat(fixture, 12).ToArray()) < 0 && Physics.Lines == 8,
    "dense occluded lights defer to SAIN within the ray budget");
Physics.Wall = false;
Check(ExposureResponse.Check(2, 20, false, false, 0, -10, -10, false, false) == ExposureReason.Sample,
    "unseen targets have no implicit memory at raid start");
Check(ExposureResponse.Check(10, 20, false, true, 15, 15, 15, false, false) == ExposureReason.Sample,
    "future timestamps do not grant memory");
TOD_Sky.Instance = new() { SunDirection = Vector3.up };
Physics.Roof = true;
UnityEngine.Object.Scene = [];
Advance(6);
p.Update();
p.diagnostics.Fail = true;
Near(Plugin.Sight(local, 2), 2.56f, "diagnostic failure preserves gameplay");
p.End();
Check(p.SinkErrors == 2, "detail and summary sink errors isolated");
AiExposureTests.Run(false, Check);
AiExposureTests.Run(true, Check);
AiExposureTests.RunCrowdedScene(Check);
Console.WriteLine($"Local exposure: {checks} checks passed.");
