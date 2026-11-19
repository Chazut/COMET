using System;
using System.Collections.Generic;
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
