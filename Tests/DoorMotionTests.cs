using Comet.Core;
using Comet.Server;

internal static class DoorMotionTests
{
    internal static int Run()
    {
        int n = 0;
        void Check(bool v, string why)
        {
            n++;
            if (!v)
                throw new Exception("C04 " + why);
        }
        var config = new DoorMotionConfig();
        config.Validate();
        Check(
            config.Enabled
                && config.PushAway
                && config.CarveOpenLeaves
                && config.StuckDelay == 1.5
                && config.LeafDistance == 1.2
                && config.SwingDuration == .5
                && config.HumanClearance == 2.5,
            "zzap defaults"
        );
        foreach (
            var edit in new Action<DoorMotionConfig>[]
            {
                x => x.StuckDelay = double.NaN,
                x => x.LeafDistance = 9,
                x => x.SwingDuration = 0,
                x => x.HumanClearance = -1,
            }
        )
        {
            var c = new DoorMotionConfig();
            edit(c);
            bool rejected = false;
            try
            {
                c.Validate();
            }
            catch (ArgumentException)
            {
                rejected = true;
            }
            Check(rejected, "invalid config rejected");
        }
        foreach (double shut in new[] { -30d, 0d, 30d })
        foreach (double open in new[] { -110d, -82d, 82d, 110d })
        {
            double end = shut + open,
                opposite = DoorMotion.Away(shut, end);
            Check(DoorMotion.Reversed(shut, end, opposite), "reversed relative to closed angle");
            Check(!DoorMotion.Reversed(shut, end, end), "normal leaf never reversed");
            Check(
                Math.Abs(DoorMotion.At(opposite, end, .25, .5) - shut) < .001,
                "unjam travels through closed plane, including angles over 90 degrees"
            );
            Check(
                Math.Abs(DoorMotion.At(opposite, end, 1, .5) - end) < .001,
                "correction ends at normal angle"
            );
        }
        Check(!DoorMotion.Reversed(0, 90, double.NaN), "invalid geometry excluded");
        var old = Newtonsoft.Json.JsonConvert.DeserializeObject<CometConfig>(
            "{\"doorMotion\":null}"
        )!;
        old.Validate();
        Check(old.DoorMotion.Enabled && old.DoorMotion.CarveOpenLeaves, "old config migration");
        string path = Path.Combine(Path.GetTempPath(), "comet-c04-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ConfigStore(path);
            var c = store.Read();
            c.DoorMotion.PushAway = false;
            c.DoorMotion.CarveOpenLeaves = false;
            c.DoorMotion.SwingDuration = .75;
            store.Save(c, c.Revision);
            var read = new ConfigStore(path).Read();
            Check(
                !read.DoorMotion.PushAway && !read.DoorMotion.CarveOpenLeaves && read.DoorMotion.SwingDuration == .75,
                "server save/reload"
            );
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".bak");
        }
        Console.WriteLine($"C04 geometry/config: {n} checks passed");
        return n;
    }
}
