using System;
using System.Collections.Generic;
using System.Linq;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client
{
    public static class Cases
    {
        static int checks;
        static void Check(bool ok, string why) { checks++; if(!ok) throw new Exception("Enemy paths: " + why); }
        static bool Near(double a, double b) => Math.Abs(a-b)<.0001;
        public static void Run()
        {
            Vector3 start=new(100,10,200), corner=new(103,10,200), end=new(103,14,200);
            var bot=new Bot { Position=start };
            var enemy=new Enemy { KnownPlaces=new(){ LastKnownPosition=end } };
            var path=enemy.Path;
            var config=new Config();
            foreach(int capacity in new[]{3,64,128,256,1024})
            foreach(var tail in new[]{new Vector3(),new Vector3(-999,900,800),new Vector3(float.NaN,float.NaN,float.NaN)})
            {
                path.PathCorners=Enumerable.Repeat(tail,capacity).ToArray();
                path.PathCorners[0]=start;path.PathCorners[1]=corner;path.PathCorners[2]=end;
                path.PathCornerCount=3;path.PathLength=7;path.PathToEnemy!.status=NavMeshPathStatus.PathComplete;
                var slice=EnemyPathCorners.Read(path);
                Check(slice.Length==3 && slice[2]==end,"only populated prefix is exposed, regardless of capacity/tail");
                var available=Probe.Capabilities(bot,enemy,config);
                Check(!available.Unavailable.ContainsKey(Tactic.Push)&&!available.Unavailable.ContainsKey(Tactic.Search),"A01 push and search accept actual endpoint");
                var metric=Probe.Opportunity(enemy);
                Check(metric.Count==3 && Near(metric.Length,7) && Near(metric.Vertical,4),"A10 travel length and vertical travel exclude tail");
                var quiet=Probe.Quiet(bot,enemy);
                Check(quiet.Complete && Near(quiet.Length,7),"P02 approach accepts and measures counted path");
                Probe.Samples.Clear();
                double risk=Probe.RouteRisk(slice,start,end,new(),30,1,out var samples);
                Check(Near(risk,.5) && samples.All(x=>x==1),"A02 route risk uses seven metres, no tail detour");
                Check(Probe.Samples.Count==2 && Near(Probe.Samples[0].x,102+1d/3) && Near(Probe.Samples[0].y,10)
                    && Near(Probe.Samples[1].x,103) && Near(Probe.Samples[1].y,11+2d/3),"A02 samples are on actual route");
                // SAIN keeps old corners after shrinking or clearing the route.
                path.PathCornerCount=2;enemy.KnownPlaces.LastKnownPosition=corner;
                Check(!Probe.Capabilities(bot,enemy,config).Unavailable.ContainsKey(Tactic.Push),"shortened route ignores previous endpoint");
                Check(Near(Probe.Opportunity(enemy).Length,3)&&Near(Probe.Quiet(bot,enemy).Length,3),"shorter route not measured using old tail");
                path.PathCornerCount=0;
                Check(EnemyPathCorners.Read(path).IsEmpty,"Clear count discards all old points");
                Check(Probe.Capabilities(bot,enemy,config).Unavailable.ContainsKey(Tactic.Search)&&!Probe.Quiet(bot,enemy).Complete,"cleared path cannot enable movement");
                Check(Probe.Opportunity(enemy)==(0,0d,0d),"cleared path cannot supply old travel estimates");
                enemy.KnownPlaces.LastKnownPosition=end;
            }
            path.PathCorners=new[]{start,corner,end};path.PathCornerCount=3;
            foreach(var status in new[]{NavMeshPathStatus.PathPartial,NavMeshPathStatus.PathInvalid})
            {
                path.PathToEnemy!.status=status;
                Check(Probe.Capabilities(bot,enemy,config).Unavailable.Count==2&&!Probe.Quiet(bot,enemy).Complete,"partial/invalid routes remain blocked");
            }
            path.PathToEnemy=null;
            Check(Probe.Capabilities(bot,enemy,config).Unavailable.Count==2&&!Probe.Quiet(bot,enemy).Complete,"missing native path remains blocked");
            path.PathToEnemy=new();
            foreach(int count in new[]{-1,0,1,4,int.MaxValue})
            {
                path.PathCornerCount=count;
                Check(Probe.Capabilities(bot,enemy,config).Unavailable.Count==2&&!Probe.Quiet(bot,enemy).Complete,"invalid or too-short prefix fails closed: "+count);
            }
            path.PathCornerCount=3;path.PathCorners=null;
            Check(EnemyPathCorners.Read(path).IsEmpty && EnemyPathCorners.Read(null).IsEmpty,"null sources are empty");
            path.PathCorners=new[]{start,corner,end};
            enemy.KnownPlaces.LastKnownPosition=end+new Vector3(0,0,2.01f);
            Check(Probe.Capabilities(bot,enemy,config).Unavailable.Count==2&&!Probe.Quiet(bot,enemy).Complete,"stale destination safety preserved");
            enemy.KnownPlaces.LastKnownPosition=end;bot.Position=start+new Vector3(0,0,4.01f);
            Check(Probe.Capabilities(bot,enemy,config).Unavailable.Count==2&&!Probe.Quiet(bot,enemy).Complete,"stale start safety preserved");
            bot.Position=start;path.PathLength=(float)config.PushMaxDistance+1;
            var distant=Probe.Capabilities(bot,enemy,config);
            Check(distant.Unavailable[Tactic.Push]=="push-path-too-long"&&!distant.Unavailable.ContainsKey(Tactic.Search),"push distance still limited independently of search");
            enemy.KnownPlaces.LastKnownPosition=null;
            Check(!Probe.Quiet(bot,enemy).Complete,"quiet approach requires a known destination");
            enemy.KnownPlaces.LastKnownPosition=end;
            path.PathCorners=new Vector3[256];path.PathCornerCount=129;
            Check(Probe.Opportunity(enemy).Count==0&&!Probe.Quiet(bot,enemy).Complete,"bounded travel processing still rejects excessive actual count");
            Check(Probe.RouteRisk(EnemyPathCorners.Read(path),start,end,new(),30,1,out _)==1,"bounded risk calculation rejects excessive actual count");
            path.PathCorners=new[]{start,corner,end};path.PathCornerCount=3;
            // A borrowed slice must not allocate in the per-bot hot path.
            for(int i=0;i<10000;i++)_ = EnemyPathCorners.Read(path).Length;
            long before=GC.GetAllocatedBytesForCurrentThread();int sum=0;
            for(int i=0;i<10000;i++)sum+=EnemyPathCorners.Read(path).Length;
            Check(sum==30000&&GC.GetAllocatedBytesForCurrentThread()==before,"10,000 counted reads allocate zero bytes");
            Console.WriteLine($"Enemy path regression: {checks} checks passed using production A01/A02/A10/P02 calculations; engine objects doubled, no raid simulation.");
        }
    }
    internal enum Tactic { Push,Search }
    internal sealed class Situation { internal Dictionary<Tactic,string> Unavailable=new(); }
    internal sealed class Config { internal double PushMaxDistance=40; }
    internal sealed class KnownThreat { }
    internal sealed class Bot { internal Vector3 Position; }
    internal sealed class Places { internal Vector3? LastKnownPosition; }
    internal sealed class Enemy { internal SAINEnemyPath Path=new(); internal Places KnownPlaces=new(); }
}
namespace SAIN.SAINComponent.Classes.EnemyClasses
{
    internal sealed class SAINEnemyPath
    {
        internal Vector3[]? PathCorners;internal int PathCornerCount;internal float PathLength;
        internal NavMeshPath? PathToEnemy=new();
    }
}
namespace UnityEngine.AI
{
    internal enum NavMeshPathStatus { PathComplete,PathPartial,PathInvalid }
    internal sealed class NavMeshPath { internal NavMeshPathStatus status; }
}
namespace UnityEngine
{
    internal readonly record struct Vector3(float x,float y,float z)
    {
        internal float sqrMagnitude=>x*x+y*y+z*z;
        public static Vector3 operator -(Vector3 a,Vector3 b)=>new(a.x-b.x,a.y-b.y,a.z-b.z);
        public static Vector3 operator +(Vector3 a,Vector3 b)=>new(a.x+b.x,a.y+b.y,a.z+b.z);
        internal static float Distance(Vector3 a,Vector3 b)=>MathF.Sqrt((a-b).sqrMagnitude);
        internal static Vector3 Lerp(Vector3 a,Vector3 b,float t)=>new(a.x+(b.x-a.x)*t,a.y+(b.y-a.y)*t,a.z+(b.z-a.z)*t);
    }
}
