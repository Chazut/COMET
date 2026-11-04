using SAIN.Preset.Shared.GlobalSettings;
namespace SAIN.Preset {
 // The holder is doubled; settings and JSON use the official SAIN assembly.
 public sealed class SAINPresetClass {
  public GlobalSettingsClass? GlobalSettings {get;set;}
  public Definition? Info {get;set;}=new();
 }
 public sealed class Definition {public string Name="Test";}
}
namespace SAIN.Plugin {public static class PresetHandler {public static SAIN.Preset.SAINPresetClass? LoadedPreset;}}
namespace SAIN {public static class SAINPlugin {public static Preset.SAINPresetClass? LoadedPreset=>Plugin.PresetHandler.LoadedPreset;}}
namespace UnityEngine {public static class Time {public static float realtimeSinceStartup;}}
namespace Comet.Client {
 public sealed partial class Plugin {
  internal Comet.Core.CometConfig? config=new(){Revision=17};internal bool Authority=true;
  internal string session="preset-test";internal long sequence;
  internal Diagnostics? diagnostics=null;
  internal sealed class Log {internal readonly List<string> Messages=new();internal void LogInfo(string text)=>Messages.Add(text);}
  internal readonly Log Logger=new();
  private void SafeFaultLog(string text)=>Logger.Messages.Add(text);
  private void WarnFaultSink(Exception ex)=>Logger.Messages.Add(ex.Message);
  internal void Tick(float now){UnityEngine.Time.realtimeSinceStartup=now;SampleSainPreset();}
  internal void Reset()=>ClearSainPreset();
  internal void ReportDifficulty()=>ReportDifficultyPreset();
 }
}
