using System;
using System.Collections.Generic;
using System.Linq;
using Comet.Core;

// Reference: R_F SAIN zzap fbb86b838e0fa692b759b2c0b11fc30158a53876.
internal static class LoadoutTests
{
    internal static int Run()
    {
        int n = 0;
        void Check(bool ok, string why)
        {
            n++;
            if (!ok)
                throw new Exception("Loadout: " + why);
        }
        void Near(double actual, double expected, string why) =>
            Check(Math.Abs(actual - expected) < 1e-9, why);

        var c = new CometConfig().Loadout;
        LoadoutTrace Kit(
            string cls,
            bool optic = false,
            bool dot = false,
            bool bolt = false,
            bool auto = false,
            int rpm = 600,
            string ammo = ""
        ) => LoadoutModel.Classify(cls, bolt, auto, rpm, optic, dot, ammo, c);
        foreach (
            var x in new[]
            {
                ("pistol", 8d),
                ("shotgun", 8d),
                ("smg", 12d),
                ("assaultRifle", 20d),
                ("marksmanRifle", 30d),
                ("sniperRifle", 40d),
                ("custom-class", 20d),
            }
        )
            Near(Kit(x.Item1).IdealRange, x.Item2, "source iron range");
        Near(Kit("assaultRifle", true, true).IdealRange, 35, "scope takes precedence over red dot");
        Near(Kit("assaultRifle", false, true).IdealRange, 25, "red-dot range");
        Near(Kit("marksmanRifle", true).IdealRange, 50, "DMR scope");
        Near(Kit("sniperRifle", true).IdealRange, 70, "sniper scope");
        Near(
            Kit("assaultRifle", true, bolt: true).IdealRange,
            60,
            "custom bolt minimum with scope"
        );
        Near(Kit("assaultRifle", bolt: true).IdealRange, 35, "custom bolt iron minimum");
        Near(Kit("shotgun", ammo: "slug").IdealRange, 8, "no COMET slug range override");
        Near(Kit("sniperRifle").Cqb, .25, "non-bolt sniper class");
        Near(Kit("marksmanRifle", bolt: true).Cqb, 0, "bolt CQB");
        Near(Kit("smg").Cqb, 1, "SMG CQB");
        Near(Kit("assaultRifle", auto: true, rpm: 700).Cqb, .9, "fast full auto");
        Near(Kit("assaultRifle", auto: true, rpm: 699).Cqb, .7, "slow full auto");
        Near(Kit("marksmanRifle", true).Long, .8, "DMR long score");
        Near(Kit("sniperRifle", true).Long, 1, "sniper long score");
        Near(Kit("smg").Long, .05, "SMG long score");
        var a = new Situation
        {
            Health = 1,
            Ammo = 1,
            PathLength = 10,
            KnownDistance = 10,
            Loadout = Kit("smg"),
        };
        var b = new Situation
        {
            Health = 1,
            Ammo = 1,
            PathLength = 10,
            KnownDistance = 10,
            Loadout = Kit("sniperRifle", true, bolt: true),
        };
        Check(
            ZzapUtility.Hidden(a, .5, 0, false)[Tactic.Push]
                > ZzapUtility.Hidden(b, .5, 0, false)[Tactic.Push],
            "source utility uses equipment profile directly"
        );
        var refresh = new LoadoutRefresh();
        Check(refresh.NeedsRefresh(10, "a", "ammo"), "first capture");
        refresh.Complete(10, "a", "ammo", 5);
        Check(
            !refresh.NeedsRefresh(14.99, "a", "ammo") && refresh.NeedsRefresh(15, "a", "ammo"),
            "five-second refresh"
        );
        Check(refresh.NeedsRefresh(11, "b", "ammo"), "new weapon refresh");
        var trace = Kit("smg");
        var copy = trace.Copy();
        trace.Grenades = 9;
        Check(copy.Grenades == 0, "RR snapshot independent");

        return n;
    }
}
