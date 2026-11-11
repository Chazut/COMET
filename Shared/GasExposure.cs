using System;
namespace Comet.Core;
public sealed class GasExposureConfig
{
 public bool Enabled {get;set;}=true;
 public double RampIn {get;set;}=4;
 public double Recovery {get;set;}=8;
 public double Refresh {get;set;}=.25;
 public double CoughInterval {get;set;}=3.5;
 public double AttemptDuration {get;set;}=5;
 public double RetryDelay {get;set;}=2;
 public void Validate(){CometConfig.Range(RampIn,1,15,nameof(RampIn));CometConfig.Range(Recovery,1,30,nameof(Recovery));CometConfig.Range(Refresh,.1,.5,nameof(Refresh));CometConfig.Range(CoughInterval,1,15,nameof(CoughInterval));CometConfig.Range(AttemptDuration,1,10,nameof(AttemptDuration));CometConfig.Range(RetryDelay,.5,5,nameof(RetryDelay));}
}
public sealed class GasExposureTrace
{
 public string State="exposed",Reason="gas-exposure",Provider="Manimal-CSGas",Variant="",Cloud="",Protection="None";
 public double Intensity,X,Y,Z,GoalX,GoalY,GoalZ,ValidFor=1;
 public int Clouds;
 public bool Inside,HasGoal,Cough;
 public string[] Alternatives=Array.Empty<string>();
}
public static class GasExposureRules
{
 // No catch-up penalty after sleep, suspension or a long frame. Overlapping clouds never stack.
 public static double Exposure(double previous,bool inside,string protection,double dt,GasExposureConfig c)
 {
  if(double.IsNaN(previous)||double.IsInfinity(previous))previous=0;
  if(double.IsNaN(dt)||double.IsInfinity(dt)||dt<0)dt=0;
  dt=Math.Min(.5,dt);
  return Math.Max(0,Math.Min(1,previous+(inside&&protection!="Full"?dt/c.RampIn:-dt/c.Recovery)));
 }
 public static bool Escape(bool inside,string protection)=>inside&&protection!="Full";
 public static bool Cough(bool inside,string protection,double intensity)=>inside&&protection=="None"&&intensity>0;
}
