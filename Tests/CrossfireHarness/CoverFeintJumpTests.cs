using Comet.Client;
using UnityEngine;

internal static class CoverFeintJumpTests
{
    public static void Run()
    {
        var origin = new Vector3(0, 2, 0);
        var landing = new Vector3(.2f, 2.25f, 0);
        var velocity = new Vector3(1.2f, 8, 0);
        var rays = new List<Vector3>();
        bool overheadAtOrigin = true, overheadAtLanding = true;
        var previousRay = Physics.Ray;
        try
        {
            Physics.Ray = (from, direction) =>
            {
                if (direction != Vector3.up)
                    throw new Exception("M14 headroom ray must point up");
                rays.Add(from);
                return (from.x == origin.x ? overheadAtOrigin : overheadAtLanding, .2f);
            };
            void Check(bool condition, string scenario)
            {
                if (!condition)
                    throw new Exception("M14 run-up: " + scenario);
            }
            bool Clear() => CoverFeintJump.HasClearRunUp(origin, landing, velocity);
            Check(!Clear() && rays.Count == 1, "blocked takeoff");
            rays.Clear();
            overheadAtOrigin = false;
            Check(!Clear() && rays.SequenceEqual(new[] { origin + Vector3.up * 1.7f, landing + Vector3.up * 1.7f }),
                "blocked landing");
            overheadAtLanding = false;
            Check(Clear(), "headroom restored on the same leg at the speed and height boundaries");
            landing.y = 2.251f;
            Check(!Clear(), "step too high");
            landing.y = 1.749f;
            Check(!Clear(), "drop too large");
            landing.y = 2;
            velocity.x = 1.19f;
            Check(!Clear(), "vertical velocity does not contribute to forward speed");
            velocity.x = -2;
            Check(!Clear(), "running away from landing");
            velocity.x = 2;
            landing.x = .005f;
            Check(!Clear(), "no horizontal run-up direction");
            Console.WriteLine("M14 jump run-up and clearance: 8 checks passed.");
        }
        finally
        {
            Physics.Ray = previousRay;
        }
    }
}
