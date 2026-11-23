using System;
using System.Collections.Generic;
using System.Linq;
using Comet.Core;
using EFT.InventoryLogic;

public partial class Probe
{
    public static void Run()
    {
        int checks = 0;
        void C(bool ok, string why)
        {
            checks++;
            if (!ok)
                throw new Exception(why);
        }
        WeaponRangeGun Read(string cls, params Mod[] mods) =>
            ReadRangeGun(
                new Weapon { WeapClass = cls, Mods = mods },
                new WeaponReturnGun
                {
                    Id = "gun",
                    Loaded = 30,
                    Usable = true,
                }
            );
        C(Read("sniperRifle").LongScore == 100, "source sniper class");
        C(Read("marksmanRifle").LongScore == 100, "source marksman class");
        C(
            Read("assaultRifle", new Scope()).LongScore == 0,
            "scope alone does not classify assault rifle as long gun"
        );
        C(
            Read("sniperRifle", new Mod { Sight = new() }).LongScore == 100,
            "unknown optic does not veto sniper"
        );
        RunParity();
        Console.WriteLine(
            $"W05 equipment production methods: {checks} checks passed (native types doubled)."
        );
    }
}

namespace EFT.InventoryLogic
{
    public class Mod
    {
        public string Id = Guid.NewGuid().ToString(),
            TemplateId = "fixture-template";
        public object Template = new();
        public SightComponent? Sight;

        public T? GetItemComponent<T>()
            where T : class => Sight as T;
    }

    public class Iron : Mod { }

    public class Scope : Mod { }

    public class Dot : Mod { }

    public class ScopeTemplate { }

    public class SightComponent { }

    public class Weapon
    {
        public string Id = "";
        public int Loaded, Reserve;
        public string ShortName = "fixture rifle",
            WeapClass = "assaultRifle";
        public bool BoltAction;
        public EFireMode[] WeapFireType = Array.Empty<EFireMode>();
        public IEnumerable<Mod> Mods = Array.Empty<Mod>();

        public enum EFireMode
        {
            fullauto,
        }
    }

    public static class JsonTypes
    {
        public static Dictionary<string, Type> TypeTable = new(),
            TemplateTypeTable = new();
    }

    public static class Localization
    {
        public static string Localized(this string s) => s;
    }
}

class Enemy
{
    public string EnemyProfileId = "";
    public bool IsVisible, Seen;
    public float TimeSinceSeen, RealDistance;
}
class EnemyController { public List<Enemy?> KnownEnemies = new(); }
enum EquipmentSlot { FirstPrimaryWeapon, SecondPrimaryWeapon, Holster, Other }
class Selector { public EquipmentSlot _mainWeapon, EquipmentSlot; }
class Manager { public Selector Selector = new(); public Manager Reload => this; public bool Reloading; }
class Owner { public Manager WeaponManager = new(); }
class Player
{
    public object? HandsController;
    public class FirearmController { public Weapon Item = new(); }
}
class BotComponent
{
    public Enemy? GoalEnemy;
    public EnemyController EnemyController = new();
    public Owner BotOwner = new();
    public Player Player = new();
    public Weapon?[] Slots = new Weapon?[3];
}
class BotState
{
    internal BotState WeaponReturn => this;
    internal WeaponReturnPolicy Policy = new();
    internal BotState ReloadTiming => this;
    internal BotState ReturnInventory => this;
    internal IDisposable Begin() => new Lease();
    class Lease : IDisposable { public void Dispose() { } }
}
static class Time { public static float time; }
static class PerformanceCapture { public static CombatWorkCounts Work = new(); }

public partial class Probe
{
    private static Weapon? SlotGun(BotComponent b, EquipmentSlot slot) => b.Slots[(int)slot];
    private static WeaponReturnGun ReadReturnGun(BotComponent b, IDisposable _, EquipmentSlot slot)
    {
        var w = SlotGun(b, slot);
        return w == null ? new() : new() { Id = w.Id, Slot = slot.ToString(), Loaded = w.Loaded, Reserve = w.Reserve };
    }
    private static bool WeaponHandsReady(BotComponent b, Player.FirearmController? firearm) => firearm != null;
    private static WeaponReturnConfig RangeArbiterConfig() => new();

    private static void RunParity()
    {
        var probe = new Probe();
        var b = new BotComponent();
        var botState = new BotState();
        var random = new Random(506);
        var c = new WeaponRangeConfig();
        var eager = new WeaponRangePolicy();
        var live = new WeaponRangePolicy();
        var hands = new Player.FirearmController();
        b.Player.HandsController = hands;
        int switches = 0;
        var counts = PerformanceCapture.Work;
        for (int tick = 0; tick < 16000; tick++)
        {
            Time.time = tick * .5f;
            Weapon? Gun(int slot, string cls) => random.Next(6) == 0 ? null : new Weapon
            {
                Id = "w" + slot, WeapClass = cls, Loaded = random.Next(3), Reserve = random.Next(2),
            };
            b.Slots[0] = Gun(0, "assaultRifle");
            b.Slots[1] = Gun(1, "sniperRifle");
            b.Slots[2] = Gun(2, "pistol");
            int mainSlot = random.Next(3) == 0 ? 1 : 0;
            b.BotOwner.WeaponManager.Selector._mainWeapon = (EquipmentSlot)mainSlot;
            var held = b.Slots[random.Next(4) % 3];
            hands.Item = held ?? new Weapon { Id = "unknown" };
            var known = b.EnemyController.KnownEnemies;
            known.Clear();
            Enemy Contact() => new Enemy
            {
                EnemyProfileId = "e" + random.Next(3), IsVisible = random.Next(3) == 0,
                Seen = random.Next(2) == 0, TimeSinceSeen = new[] { 1, 2, 20, 21 }[random.Next(4)],
                RealDistance = new[] { 44, 45, 79, 80, 100 }[random.Next(5)],
            };
            for (int i = random.Next(6); i > 0; i--) known.Add(random.Next(7) == 0 ? null : Contact());
            b.GoalEnemy = random.Next(3) switch { 0 => null, 1 => known.FirstOrDefault(), _ => Contact() };

            var frame = probe.WeaponRangeFrameFor(b, botState, hands, out var main, out var other);
            var all = known.Where(e => e != null).Select(e => e!).ToList();
            if (b.GoalEnemy != null && !all.Any(e => e.EnemyProfileId == b.GoalEnemy.EnemyProfileId))
                all.Add(b.GoalEnemy);
            var expectedFrame = new WeaponRangeFrame
            {
                Now = Time.time, Main = b.Slots[mainSlot]?.Id ?? "",
                Current = b.Slots.Take(2).Any(w => w?.Id == hands.Item.Id)
                    ? hands.Item.Id : b.Slots[2]?.Id ?? "",
                Ready = true, Goal = b.GoalEnemy?.EnemyProfileId ?? "",
                Weapons = new[] { mainSlot, 1 - mainSlot }.Where(i => b.Slots[i] != null)
                    .Select(i => ReadRangeGun(b.Slots[i]!, ReadReturnGun(b, null!, (EquipmentSlot)i))).ToArray(),
                Contacts = all.Select(e => new WeaponRangeContact { Id = e.EnemyProfileId,
                    Age = e.IsVisible ? 0 : e.Seen ? e.TimeSinceSeen : 999, Distance = e.RealDistance }).ToArray(),
            };
            string got = live.Evaluate(frame, c, new NativeRangeContacts(b));
            string want = eager.Evaluate(expectedFrame, c);
            var a = live.Trace!; var z = eager.Trace!;
            if (got != want || (a.From, a.Target, a.Distance, a.ContactAge, a.Reason, a.Mode, a.To) !=
                (z.From, z.Target, z.Distance, z.ContactAge, z.Reason, z.Mode, z.To)
                || live.ProtectCurrent != eager.ProtectCurrent
                || !a.Weapons.Select(g => (g.Id,g.Class,g.Loaded,g.Reserve)).SequenceEqual(
                    z.Weapons.Select(g => (g.Id,g.Class,g.Loaded,g.Reserve))))
                throw new Exception("weapon or evidence mismatch at " + tick);
            if (got.Length > 0)
            {
                switches++;
                if (got != main.Id && got != other.Id) throw new Exception("unresolvable weapon request");
                var ack = new WeaponReturnTrace { To = got, Accepted = random.Next(5) != 0 };
                live.Arbitration(ack, Time.time);
                eager.Arbitration(ack, Time.time);
            }
        }
        if (switches == 0 || counts.RangeOtherContactScans == 0 || counts.RangeOtherContactScans >= counts.RangeEvaluations)
            throw new Exception("missing switching or retained-long coverage");

        var before = new WeaponReturnPolicy();
        var cfg = RangeArbiterConfig();
        var selector = b.BotOwner.WeaponManager.Selector;
        Time.time = 1;
        void Observe(string id, EquipmentSlot slot, bool armed)
        {
            selector.EquipmentSlot = slot;
            hands.Item.Id = id;
            b.Player.HandsController = armed ? hands : null;
            probe.ObserveWeaponReturn(b, botState);
            before.Observe(Time.time, armed ? id : "", slot.ToString(), armed, cfg);
            var a = before.Trace; var z = botState.Policy.Trace;
            if ((a?.State, a?.Reason, a?.InHands, a?.Ready) != (z?.State, z?.Reason, z?.InHands, z?.Ready)
                || before.PistolBlockedFor(Time.time) != botState.Policy.PistolBlockedFor(Time.time))
                throw new Exception("transition observation mismatch");
        }
        Observe("p", EquipmentSlot.Holster, true);
        Time.time = 4;
        Observe("a", EquipmentSlot.FirstPrimaryWeapon, true);
        if (botState.Policy.PistolBlockedFor(Time.time) != cfg.PistolReturnDelay)
            throw new Exception("manual holster exit skipped");
        foreach (var policy in new[] { before, botState.Policy })
        {
            policy.Evaluate(new WeaponReturnFrame { Current = new WeaponReturnGun { Id = "a" } },
                new WeaponReturnGun { Id = "b" }, "W05", cfg);
            policy.Request(Time.time);
        }
        Observe("a", EquipmentSlot.FirstPrimaryWeapon, false);
        Observe("b", EquipmentSlot.SecondPrimaryWeapon, true);
        if (counts.WeaponSlotObservations != 4 || counts.WeaponTransitionReads != 2)
            throw new Exception("inactive observation performed transition reads");
        Console.WriteLine($"16000 weapon decisions matched ({switches} requests, {counts.RangeOtherContactScans} secondary scans).");
    }
}
