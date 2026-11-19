from pathlib import Path
import os
import subprocess

root = Path(__file__).resolve().parents[2]
file = "Client/CornerPeekAdapter.cs"
ref = os.environ.get("BASELINE_REF")
text = (
    subprocess.check_output(
        ["git", "show", f"{ref}:{file}"], cwd=root, text=True, encoding="utf-8"
    )
    if ref
    else (root / file).read_text(encoding="utf-8-sig")
)
start = text.index("    private void PlanCornerPeek(")
end = text.index("    private bool CornerRoute(", start)
Path(__file__).with_name("Production.Generated.cs").write_text(
    "using Comet.Core; using UnityEngine;\nnamespace Comet.Client;\npublic partial class Plugin {\n"
    + text[start:end]
    + "\n}",
    encoding="utf-8",
)
