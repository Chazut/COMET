from pathlib import Path
import sys

root = Path(__file__).resolve().parents[2]
source = (root / "Client/MovementHearingAdapter.cs").read_text(encoding="utf-8")
fields = source[source.index("    private const int MovementQueueLimit"):source.index("    private sealed class MovementCacheState")]

def method(name, signature="private void"):
    start = source.index("    " + signature + " " + name + "(")
    end = source.index("\n    }", start)
    return source[start:end + 6]

output = Path(sys.argv[1])
output.parent.mkdir(parents=True, exist_ok=True)
output.write_text("#nullable enable annotations\n#nullable disable warnings\n#pragma warning disable CS0649, CS0169\nusing Comet.Core; using System.Numerics;\npartial class Plugin {\n" + fields + "\n"
                  + "\n".join(method(name) for name in ("UpdateMovementHearing", "PruneMovementHearing",
                               "SnapshotMovementRecipients", "DeliverMovementSound")) + "\n"
                  + method("MovementLevels", "private static (string? Gait, float Range, float Volume)") + "\n"
                  + method("GaitHearingPrefix", "private static bool") + "\n}", encoding="utf-8")
