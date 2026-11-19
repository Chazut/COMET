namespace Comet.Server.Web;

// Browser presentation only. Deliberately has no access to configuration or gameplay.
public sealed class SecretBrand
{
    public const string StorageKey = "comet.ui.slop.v1";
    private int clicks;
    private long started;
    public bool Slop { get; private set; }
    public bool Changed { get; private set; }

    public void Restore(string value)
    {
        // A delayed browser read must not undo a click received during startup.
        if (!Changed)
            Slop = value == "1";
    }

    public bool Click(long nowMilliseconds)
    {
        if (clicks == 0 || nowMilliseconds - started > 4000 || nowMilliseconds < started)
        {
            clicks = 0;
            started = nowMilliseconds;
        }
        if (++clicks < 5)
            return false;
        Set(!Slop);
        return true;
    }

    public void Set(bool value)
    {
        Slop = value;
        Changed = true;
        clicks = 0;
    }
}
