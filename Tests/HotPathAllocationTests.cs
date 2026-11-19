using Comet.Core;

static class HotPathAllocationTests
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool value, string why)
        {
            checks++;
            if (!value)
                throw new Exception(why);
        }
        // The former two-pass selection is the behavioral oracle. Exercise empty,
        // loaded, reloadable, unusable and current-weapon cases for every slot.
        static string Previous(WeaponReturnFrame f)
        {
            if (f.Current.Loaded > 0)
                return "";
            foreach (var g in new[] { f.Main, f.Other, f.Pistol })
                if (g.Id != f.Current.Id && g.Loaded > 0)
                    return g.Id;
            foreach (var g in new[] { f.Main, f.Other, f.Pistol })
                if (g.Id != f.Current.Id && g.Reloadable)
                    return g.Id;
            return "";
        }
        static WeaponReturnGun Gun(string id, int state) =>
            new()
            {
                Id = id,
                Loaded = state == 1 ? 1 : 0,
                Reserve = state >= 2 ? 30 : 0,
                Resource = state == 4 ? "none" : "magazine",
                Usable = state != 3,
            };
        var policy = new WeaponReturnPolicy();
        foreach (var current in new[] { "main", "other", "pistol", "unknown", "" })
        foreach (var loaded in new[] { 0, 1 })
            for (int main = 0; main < 5; main++)
            for (int other = 0; other < 5; other++)
            for (int pistol = 0; pistol < 5; pistol++)
            {
                var f = new WeaponReturnFrame
                {
                    Current = new() { Id = current, Loaded = loaded },
                    Main = Gun("main", main),
                    Other = Gun("other", other),
                    Pistol = Gun("pistol", pistol),
                };
                Check(policy.Fallback(f) == Previous(f), "Weapon fallback changed source ordering");
            }
        var frame = new WeaponReturnFrame
        {
            Current = Gun("main", 0),
            Main = Gun("main", 0),
            Other = Gun("other", 0),
            Pistol = Gun("pistol", 2),
        };
        for (int i = 0; i < 1000; i++)
            policy.Fallback(frame);
        long before = GC.GetAllocatedBytesForCurrentThread();
        string result = "";
        for (int i = 0; i < 10000; i++)
            result = policy.Fallback(frame);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(
            bytes == 0 && result == "pistol",
            "Weapon fallback must not allocate temporary arrays"
        );
        Console.WriteLine(
            $"Hot-path parity: {checks} checks; W03 fallback {bytes} bytes / 10000 evaluations."
        );
        return checks;
    }
}
