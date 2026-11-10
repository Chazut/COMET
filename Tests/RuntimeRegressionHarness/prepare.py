from pathlib import Path
root = Path(__file__).resolve().parents[2]
def method(file, signature):
    source = (root/file).read_text(encoding='utf-8-sig')
    start = source.index(signature)
    end = source.index('{', start) + 1
    depth = 1
    while depth:
        depth += (source[end] == '{') - (source[end] == '}')
        end += 1
    return source[start:end]
movement = '\n'.join(method('Client/CombatStepAdapter.cs', name) for name in [
    '    private static bool ReleaseShootingRoute(', '    private static bool ShootingHandoffAllowed(', '    private static IBotPathData? CrossfireQueuedPath(',
    '    private static bool QueuedCombatMovement('])
inventory = method('Client/LoadoutAdapter.cs', '    internal static EquipmentSlot? RootSlot(')
inventory += '\n' + method('Client/LoadoutAdapter.cs', '    private static bool UsableMed(')
out = Path(__file__).parent/'obj/Production.Generated.cs'
out.parent.mkdir(exist_ok=True)
out.write_text('#nullable enable\nusing System.Numerics;\npartial class Runtime {\n'+movement+'\n'+inventory+'\n}\n', encoding='utf-8')
