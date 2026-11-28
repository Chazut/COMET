using Comet.Core;

static class SourceWeaponBaselineTests
{
    public static int Run(string area)
    {
        int n = 0;
        void Check(bool ok, string why)
        {
            n++;
            if (!ok)
                throw new Exception(area + ": " + why);
        }
        WeaponReturnGun Gun(string id, string slot, int loaded, int reserve = 0) =>
            new()
            {
                Id = id,
                Slot = slot,
                Loaded = loaded,
                Reserve = reserve,
                Resource = reserve > 0 ? "magazine" : "none",
                Usable = true,
            };
        if (area == "W01")
        {
            var c = new ReloadTimingConfig();
            var p = new ReloadTimingPolicy();
            var f = new ReloadTimingFrame
            {
                Now = 10,
                Weapon = "rifle",
                Capacity = 30,
                Loaded = 15,
                Visible = false,
                Distance = 40,
                SightAge = 30,
                ContactAge = 30,
            };
            p.Evaluate(f, c);
            Check(Math.Abs(p.Trace!.ReloadScore - .575) < 1e-9, "source hidden reload score");
            Check(Math.Abs(p.Trace.HoldScore - .4) < 1e-9, "source hold score");
            f.Aggression = 1;
            f.Threats = 99;
            f.ThreatsTruncated = true;
            f.Block = "old-extra-gate";
            Check(p.Evaluate(f, c) == "Reload", "extra context does not veto source choice");
            f.Visible = true;
            f.Looking = true;
            f.Distance = 5;
            Check(p.Evaluate(f, c) == "Hold", "visible close threat keeps gun up");
            f.Loaded = 0;
            p.Evaluate(f, c);
            Check(Math.Abs(p.Trace!.HoldScore - .05) < 1e-9, "empty gun source hold penalty");
            f.Visible = false;
            f.Looking = false;
            f.Distance = 20;
            f.Loaded = 15;
            f.ContactAge = .5;
            f.SightAge = 99;
            p.Evaluate(f, c);
            Check(p.Trace!.ReloadScore < .4, "just heard close threat suppresses reload score");
            f.Protected = true;
            p.Evaluate(f, c);
            Check(p.Trace!.ReloadScore > .5, "arrived cover is enough without ray proof");
            p.Refuse(f, c);
            f.Now = 14.99;
            Check(p.RetryBlocked(f), "source loaded retry five seconds");
            f.Now = 15;
            Check(!p.RetryBlocked(f), "retry boundary");
            f.Loaded = 0;
            p.Refuse(f, c);
            f.Now = 16;
            Check(!p.RetryBlocked(f), "source empty retry one second");
        }
        if (area == "W02")
        {
            var c = new PistolSwapConfig();
            var p = new PistolSwapPolicy();
            var f = new ReloadTimingFrame
            {
                Now = 5,
                Weapon = "rifle",
                Target = "enemy",
                Capacity = 100,
                Loaded = 4,
                Visible = true,
                Distance = 10,
            };
            var pistol = new PistolSwapFrame
            {
                Pistol = "pistol",
                Loaded = 1,
                PrimaryInHands = true,
                Usable = true,
            };
            var other = new ReloadTimingTrace { ReloadScore = .1, HoldScore = .2 };
            Check(
                p.Evaluate(f, pistol, other, c),
                "source score can prefer one pistol round over four rifle rounds; no round cap"
            );
            f.Visible = false;
            f.Loaded = 0;
            f.ContactAge = 1.49;
            Check(p.Evaluate(f, pistol, other, c), "empty fallback recent sight");
            f.ContactAge = 1.5;
            Check(!p.Evaluate(f, pistol, other, c), "source recent-sight boundary");
            f.Visible = true;
            p.Evaluate(f, pistol, other, c);
            p.Request(5);
            p.Result(false, 5, c);
            f.Now = 7.99;
            Check(!p.Evaluate(f, pistol, other, c), "source refused pistol three seconds");
            f.Now = 8;
            Check(p.Evaluate(f, pistol, other, c), "retry immediately at boundary");
            p.Request(8);
            p.Result(true, 8, c);
            p.Observe(8.2, "pistol", true, c);
            Check(!p.RetryBlocked(8.2), "successful draw adds no artificial retry ban");
        }
        if (area == "W03")
        {
            var c = new WeaponReturnConfig();
            var p = new WeaponReturnPolicy();
            var f = new WeaponReturnFrame
            {
                Now = 10,
                Current = Gun("pistol", "Holster", 3),
                Main = Gun("main", "FirstPrimaryWeapon", 0, 30),
                Other = Gun("other", "SecondPrimaryWeapon", 10),
                Ready = true,
                Target = "enemy",
                ContactAge = 10,
            };
            Check(p.Recommend(f, c) == "", "do not own native pistol choices");
            var draw = new WeaponReturnFrame { Now = 5, Current = f.Main };
            p.Evaluate(draw, f.Current, "W02", c);
            p.Request(5);
            p.Result(true, 5, c);
            Check(
                p.Recommend(f, c) == "main",
                "source returns to reloadable main after sight lost"
            );
            f.Incoming = true;
            Check(p.Recommend(f, c) == "", "recent shot at bot retains pistol");
            f.Far = true;
            Check(p.Recommend(f, c) == "main", "far enemy returns despite incoming fire");
            f.Busy = true;
            f.Protected = false;
            Check(p.Evaluate(f, f.Main, "return-policy", c), "no added cover/hold/busy arbiter");
            f.Main.Reserve = 0;
            Check(
                !p.Evaluate(f, f.Main, "native-return", c),
                "source rejects native empty-main loop"
            );
            f.Current.Loaded = 0;
            Check(p.Fallback(f) == "other", "loaded alternative precedes reloadable gun");
            f.Other.Loaded = 0;
            f.Main.Reserve = 30;
            Check(p.Fallback(f) == "main", "source second pass reloadable");
            p.Evaluate(f, f.Main, "return-policy", c);
            p.Request(10);
            p.Result(true, 10, c);
            Check(p.PistolBlockedFor(10) == 5, "source return gives main five seconds");
        }
        if (area == "W04")
        {
            var p = new QuickReloadPolicy();
            var c = new QuickReloadConfig();
            // Reserve: measured load (including chamber), distance and pressure at the reload request.
            // Use the recorded spare count as an upper bound on reachable, loaded spares.
            foreach (var (bot, loaded, cap, distance, age, incoming, spares, quick) in new[]
            {
                ("91-XM_", 2, 16, 81.6, .04, false, 2, false),
                ("DOUBLE2HEAD", 6, 21, 36.4, .28, false, 4, false),
                ("DOUBLE2HEAD", 0, 21, 14.2, .03, false, 5, true),
                ("DOUBLE2HEAD", 11, 21, 28.9, 1.51, false, 4, false),
                ("Olsenchiks", 5, 21, 36.6, .13, false, 2, false),
                ("Olsenchiks", 11, 21, 5.7, 3.99, true, 2, false),
                ("o-Oyue", 2, 11, 23.3, .19, false, 3, true),
            })
                Check(p.Evaluate(new QuickReloadFrame { InFight = true, OldMagazine = "inserted",
                    Loaded = loaded, Capacity = cap, Distance = distance, ContactAge = age,
                    Incoming = incoming, SpareMagazines = spares }, c) == quick,
                    "Reserve recorded drop: " + bot + " at " + distance + "m");
            var f = new QuickReloadFrame
            {
                Now = 1, InFight = true, Visible = true, OldMagazine = "inserted",
                Distance = 28.9, Capacity = 21, Loaded = 11, SpareMagazines = 2,
            };
            Check(!p.Evaluate(f, c), "DOUBLE2HEAD keeps the half-full magazine");
            f.Incoming = true;
            Check(!p.Evaluate(f, c), "Olsenchiks keeps a half-full magazine even under fire");
            f.Incoming = false;
            f.Distance = 81.6;
            f.Loaded = 2;
            Check(!p.Evaluate(f, c), "91-XM_ uses a normal reload at distant visual contact");
            f.Distance = 14.2;
            f.Loaded = 0;
            Check(p.Evaluate(f, c), "DOUBLE2HEAD can still drop an empty magazine in close contact");
            f.Visible = false;
            f.ContactAge = c.ContactMemory;
            Check(!p.Evaluate(f, c), "stale contact keeps the magazine");
            f.ContactAge = c.ContactMemory - .01;
            Check(p.Evaluate(f, c), "recent close contact still qualifies");
            f.Distance = c.ThreatDistance;
            Check(!p.Evaluate(f, c), "recent sighting at the range limit is not immediate pressure");
            f.Incoming = true;
            f.Capacity = 30;
            f.Loaded = 9;
            Check(p.Evaluate(f, c), "incoming fire qualifies at the configured ammo limit");
            f.Loaded++;
            Check(!p.Evaluate(f, c), "above the ammo limit always keeps the magazine");
            f.Loaded = 0;
            f.SpareMagazines = 1;
            Check(!p.Evaluate(f, c), "keep last spare");
            f.SpareMagazines = 2;
            f.InFight = false;
            Check(!p.Evaluate(f, c), "outside fight normal reload");
            f.InFight = true;
            f.OldMagazine = "";
            Check(!p.Evaluate(f, c), "inserting a magazine into a bare weapon never requests a drop");
        }
        if (area == "W05")
        {
            var p = new WeaponRangePolicy();
            var c = new WeaponRangeConfig();
            var a = new WeaponRangeGun
            {
                Id = "a",
                Slot = "FirstPrimaryWeapon",
                Class = "assaultRifle",
                Loaded = 1,
            };
            var b = new WeaponRangeGun
            {
                Id = "b",
                Slot = "SecondPrimaryWeapon",
                Class = "sniperRifle",
                Reserve = 10,
            };
            WeaponRangeModel.Classify(a);
            WeaponRangeModel.Classify(b);
            var f = new WeaponRangeFrame
            {
                Now = 0,
                Ready = true,
                Main = "a",
                Current = "a",
                Goal = "e",
                Weapons = new[] { a, b },
                Contacts = new[]
                {
                    new WeaponRangeContact
                    {
                        Id = "e",
                        Age = 0,
                        Distance = 80,
                    },
                },
                Incoming = true,
                SinceShot = 0,
                Truncated = true,
                Block = "old-extra-gate",
            };
            Check(
                p.Evaluate(f, c) == "b",
                "source accepts long gun immediately, even empty but reloadable, without stability/fire/optic checks"
            );
            p.Arbitration(
                new()
                {
                    To = "b",
                    Accepted = true,
                    Requested = true,
                },
                0
            );
            f.Current = "b";
            f.Now = .1;
            Check(p.Evaluate(f, c) == "" && p.ProtectCurrent, "retain deliberate long gun");
            f.Contacts[0].Distance = 44.99;
            Check(p.Evaluate(f, c) == "a", "close threshold returns to main");
            f.Contacts[0].Distance = 45;
            Check(p.Evaluate(f, c) == "", "exact close threshold keeps long gun");
            f.Contacts[0].Age = 20.01;
            Check(p.Evaluate(f, c) == "a", "twenty-second loss returns");
            f.Contacts = new[]
            {
                f.Contacts[0],
                new WeaponRangeContact
                {
                    Id = "other",
                    Age = 20,
                    Distance = 45,
                },
            };
            Check(p.Evaluate(f, c) == "", "other seen far enemy retains gun");
            p.Clear("raid-end");
            f.Current = "a";
            f.Now = 10;
            f.Contacts[0].Age = 2;
            f.Contacts[0].Distance = 80;
            Check(p.Evaluate(f, c) == "", "two-second acquisition boundary");
            f.Now = 11;
            f.Contacts[0].Age = 1.99;
            Check(p.Evaluate(f, c) == "b", "inside acquisition memory");
        }
        return n;
    }
}
