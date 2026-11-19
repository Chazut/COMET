namespace Comet.Server.Web;

public static class UiAssets
{
    static string Read(string resource)
    {
        using var stream = typeof(UiAssets).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("Missing UI asset: " + resource);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static readonly string Styles = Read("Comet.ConfigStyles.css");
    public static readonly string Logo =
        "data:image/svg+xml;base64,"
        + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(Read("Comet.Logo.svg")));
    public static readonly string SlopLogo =
        "data:image/svg+xml;base64,"
        + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(Read("Comet.SlopLogo.svg")));
}
