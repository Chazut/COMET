using Comet.Server.Web;

internal static class SecretBrandTests
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool ok, string why)
        {
            checks++;
            if (!ok)
                throw new Exception("Secret brand: " + why);
        }
        var brand = new SecretBrand();
        Check(!brand.Slop, "ordinary visitors see COMET");
        for (int i = 0; i < 4; i++)
            Check(!brand.Click(i * 100), "fewer than five clicks do nothing");
        Check(!brand.Slop && brand.Click(400) && brand.Slop, "fifth click unlocks");
        for (int i = 0; i < 4; i++)
            Check(!brand.Click(500 + i * 100), "fresh sequence after unlock");
        Check(brand.Click(900) && !brand.Slop, "five more clicks restore COMET");
        for (int i = 0; i < 4; i++)
            brand.Click(1000 + i * 100);
        Check(!brand.Click(5001) && !brand.Slop, "old navigation clicks expire");
        for (int i = 1; i < 4; i++)
            brand.Click(5001 + i * 100);
        Check(brand.Click(5401) && brand.Slop, "expired click starts a new sequence");
        brand.Set(false);
        brand.Restore("1");
        Check(!brand.Slop, "delayed storage cannot override explicit return");
        Check(!brand.Click(5500), "explicit return clears pending clicks");
        var restored = new SecretBrand();
        restored.Restore("1");
        Check(restored.Slop, "theme can be restored without editing configuration");
        restored.Restore("true");
        Check(!restored.Slop, "unexpected stored value is harmless");
        restored.Restore(null);
        Check(!restored.Slop, "empty browser storage uses COMET");
        for (int i = 0; i < 5; i++)
            restored.Click(6000 + i * 100);
        restored.Restore("0");
        Check(restored.Slop, "click during initial storage read wins");
        var otherVisitor = new SecretBrand();
        Check(!otherVisitor.Slop, "theme does not leak to other visitors");
        Console.WriteLine($"Secret brand: {checks} checks passed.");
        return checks;
    }
}
