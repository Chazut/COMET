from pathlib import Path
import sys

root = Path(__file__).resolve().parents[1]
methods = []
for source, name in [
    ("CoverExposureAdapter.cs", "NativeCoverProtects"),
    ("SeasonalCoverAdapter.cs", "FoliageCoverCollider"),
]:
    text = (root / "Client" / source).read_text(encoding="utf-8-sig")
    start = text.index("    private static bool " + name + "(")
    brace = text.find("{", start)
    arrow = text.find("=>", start)
    if 0 <= arrow < brace or brace == -1:
        end = text.index(";", arrow) + 1
    else:
        depth, end = 1, brace + 1
        while depth:
            depth += (text[end] == "{") - (text[end] == "}")
            end += 1
    methods.append(text[start:end])
output = Path(sys.argv[1]) / "obj/CoverProtection.cs"
output.parent.mkdir(exist_ok=True)
output.write_text(
    "using SAIN.Components; using UnityEngine; namespace Comet.Client; public sealed partial class Plugin {\n"
    + "\n".join(methods)
    + "\n}",
    encoding="utf-8",
)
