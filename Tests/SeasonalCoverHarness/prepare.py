from pathlib import Path

root = Path(__file__).resolve().parents[2]
text = (root / 'Client/SourceCoverContinuation.cs').read_text(encoding='utf-8-sig')
start = text.index('internal static void Forget(')
brace = text.index('{', start)
depth = 1
end = brace + 1
while depth:
    depth += (text[end] == '{') - (text[end] == '}')
    end += 1
output = Path(__file__).parent / 'obj/SourceCoverAccess.cs'
output.parent.mkdir(exist_ok=True)
output.write_text('''using SAIN.SAINComponent.Classes;
using SAIN.SAINComponent.SubComponents.CoverFinder;
namespace Comet.Client;
internal static class SourceCoverAccess {
 static void SetInUse(SAINCoverClass c, CoverPoint p) => c.CoverInUse = p;
 static void SetMovingTo(SAINCoverClass c, CoverPoint p) => c.CoverPoint_MovingTo = p;
''' + text[start:end] + '\n}', encoding='utf-8')
