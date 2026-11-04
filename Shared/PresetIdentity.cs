using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Comet.Core;

// Provenance only: these fields never control gameplay or refer to a live library object.
public sealed class PresetIdentity
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public long Revision { get; set; }
    public string SettingsHash { get; set; } = "";
    public void Validate()
    {
        if (!Guid.TryParseExact(Id, "N", out _) || Revision < 1 || Revision > 9007199254740991L)
            throw new ArgumentException("Invalid personal preset identity.");
        ValidateName(Name);
        if (SettingsHash == null || SettingsHash.Length != 64 || SettingsHash.Any(c => !"0123456789abcdef".Contains(c)))
            throw new ArgumentException("Invalid personal preset fingerprint.");
    }
    public static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || name != name.Trim()
            || name.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format))
            throw new ArgumentException("Use a preset name of 1 to 80 characters, without control characters or surrounding spaces.");
    }
    public static string Fingerprint(CometConfig config)
    {
        // Canonical, culture-independent and shared by server and client. Source/revision are excluded.
        var text = new StringBuilder();
        void Part(string value) { text.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value); }
        Part(config.DifficultyPreset);
        foreach (var pair in DifficultyPresets.Values(config))
        {
            Part(pair.Key);
            // Use IEEE bits: Mono and CoreCLR can format the same floating-point number
            // differently. Normalize signed zero, which has the same gameplay meaning.
            Part(pair.Value switch { double d => BitConverter.DoubleToInt64Bits(d == 0 ? 0 : d).ToString("x16", CultureInfo.InvariantCulture),
                float f => BitConverter.ToInt32(BitConverter.GetBytes(f == 0 ? 0 : f), 0).ToString("x8", CultureInfo.InvariantCulture),
                IFormattable v => v.ToString(null, CultureInfo.InvariantCulture), null => "<null>", _ => pair.Value.ToString()! });
        }
        using var sha = SHA256.Create();
        return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString())).Select(b => b.ToString("x2")));
    }
}
