using Comet.Core;
using System.Numerics;

enum SAINSoundType { FootStep, Sprint, Jump, Land }
enum EAudioMovementState { Run, Duck }
sealed class AIData { public int EnvironmentId = 1; public readonly Location PlayerLocation = new(); }
sealed class Location { public bool InBunker; public int BunkerDepth; }
sealed class Equipment { public readonly Gear GearInfo = new(); }
sealed class Gear { public bool HasEarPiece = true, HasHeavyHelmet; }
sealed class Memory { public readonly Health Health = new(); }
sealed class Health { public bool Dying, OnPainKillers; }
sealed class Motion { public float CovertEfficiency, ClampedSpeed = 1, MaxSpeed = 1; }
sealed class Player { public readonly AIData AIData = new(); public readonly Motion MovementContext = new(); public bool IsSprintEnabled, HeavyBreath; }
sealed class HearingAnalysis { public BotComponent Bot = null!; }
sealed class Enemy { public bool InLineOfSight = true; }
sealed class AISoundData { public SoundEvent Sound = null!; public Enemy Enemy = new(); public float PlayerDistance; public int EnvironmentId = 1; }
sealed class SAINPlugin
{
    public static SAINPlugin LoadedPreset = new();
    public SAINPlugin GlobalSettings => this;
    public SAINPlugin Hearing => this;
    public float BaseSoundRange_Footstep, BaseSoundRange_Sprint, BaseSoundRange_Jump = 15, MaxSoundRange_FallLanding = 12, FootstepAudioMultiplier;
}
sealed class SAINWeatherClass { public static SAINWeatherClass? Instance; public float RainSoundModifierOutdoor, RainSoundModifierIndoor; }
namespace EFT.Weather { sealed class WeatherController { public static WeatherController? Instance; public WeatherController? WeatherCurve => this; public float Rain; } }
static class Mathf { public static float Lerp(float a, float b, float t) => a + (b-a) * Math.Clamp(t, 0f, 1f); }
namespace UnityEngine { static class Random { public static float value; } }

partial class Plugin
{
    static Plugin? Instance;
    float floorFactor = 1f;
    float FilterFloorRange(float range, BotComponent bot, AISoundData sound, float environment, float occlusion) => range * floorFactor;

    void CheckGaitOverride()
    {
        static void Verify(bool ok, string message) { if (!ok) throw new Exception(message); }
        Instance = this;
        var settings = new MovementHearingConfig();
        var player = new Player();
        var source = new PlayerComponent();
        var bot = new BotComponent { PlayerComponent = new PlayerComponent() };
        var sound = new SoundEvent { PlayerComponent = source };
        var audio = new AISoundData { Sound = sound, PlayerDistance = 16.45f };
        var analysis = new HearingAnalysis { Bot = bot };
        var delivery = new MovementDelivery { Bot = bot, Signal = new MovementSignal { Sound = sound } };
        movementReaction = delivery;
        movementPending[(bot, sound)] = delivery;
        var other = new Other { IsInHearingRadius_Footsteps = false };
        sound.PlayerComponent.OtherPlayersData.DataHashSet.Add(other);
        SnapshotMovementRecipients(sound, true);
        Verify(movementRecipients.Count == 1, "COMET range controls candidate selection");
        SnapshotMovementRecipients(sound);
        Verify(movementRecipients.Count == 0, "native cutoff retained when using SAIN range");
        bool result = false;
        var native = SAINPlugin.LoadedPreset;
        foreach (float presetValue in new[] { 0f, 1f, 70f, 500f })
        {
            native.BaseSoundRange_Footstep = native.BaseSoundRange_Sprint = native.FootstepAudioMultiplier = presetValue;
            foreach (var (type, state, meters) in new[] {
                (SAINSoundType.FootStep, EAudioMovementState.Duck, 5f),
                (SAINSoundType.FootStep, EAudioMovementState.Run, 20f),
                (SAINSoundType.Sprint, EAudioMovementState.Run, 30f) })
            {
                var level = MovementLevels(settings, player, source, type, state, .475f);
                Verify(level.Range == meters && level.Volume == 1f, "native gain or range leaked into override");
                sound.BaseRangeWithVolume = level.Range * level.Volume;
                delivery.Signal.Gait = level.Gait;
                Verify(!GaitHearingPrefix(analysis, audio, ref result) && result == (meters >= audio.PlayerDistance)
                    && delivery.FinalRange == meters, "native cap or difficulty could execute for an owned gait");
            }
            var jump = MovementLevels(settings, player, source, SAINSoundType.Jump, EAudioMovementState.Run, .5f);
            Verify(jump == (null, 15f, .5f * presetValue), "jump retains SAIN settings");
        }
        floorFactor = .5f;
        Verify(!GaitHearingPrefix(analysis, audio, ref result) && !result, "a floor can muffle an otherwise audible sprint");
        floorFactor = 1f;
        UnityEngine.Random.value = .999f;
        Verify(!GaitHearingPrefix(analysis, audio, ref result) && !result && delivery.RangeOutcome == "chance", "in-range footsteps can be missed");
        source.Position = new Vector3(6, 0, 0);
        Verify(!GaitHearingPrefix(analysis, audio, ref result) && !result && delivery.RangeOutcome == "source-moved", "stale step");
        Verify(GaitHearingPrefix(analysis, new AISoundData { Sound = new SoundEvent() }, ref result), "unrelated event stays native");
        settings.GaitBasedRange = false;
        var off = MovementLevels(settings, player, source, SAINSoundType.FootStep, EAudioMovementState.Run, .5f);
        delivery.Signal.Gait = off.Gait;
        Verify(off == (null, 500f, 250f) && GaitHearingPrefix(analysis, audio, ref result), "OFF restores both native paths");
        movementReaction = null;
        movementPending.Clear();
        Instance = null;
        Console.WriteLine("Gait override: four SAIN settings, both toggle states, three ranges, floor, chance and stale-event checks passed.");
    }
}
