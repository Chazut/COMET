using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

// Run against a built COMET client and an extracted Winter Outbreak release. No raid or detours.
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine("Usage: WinterOutbreakAudioCheck <SPT root> <COMET.dll> <WinterOutbreak.dll>");
            return 2;
        }
        var managed = Path.Combine(args[0], "EscapeFromTarkov_Data/Managed");
        var plugins = Path.Combine(args[0], "BepInEx/plugins");
        var dirs = new[] { managed, Path.Combine(args[0], "BepInEx/core"), plugins }
            .Concat(Directory.GetDirectories(plugins, "*", SearchOption.AllDirectories)).ToArray();
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            var dll = new AssemblyName(e.Name).Name + ".dll";
            var path = dirs.Select(dir => Path.Combine(dir, dll)).FirstOrDefault(File.Exists);
            return path == null ? null : Assembly.LoadFrom(path);
        };
        Check(Assembly.LoadFrom(Path.Combine(managed, "Assembly-CSharp.dll")).GetType("EFT.Player", true),
            Assembly.LoadFrom(args[1]).GetType("Comet.Client.Plugin", true),
            Assembly.LoadFrom(args[2]).GetType("WinterOutbreak.WinterOutbreakPlugin+SnowStepPlaybackPatch", true));
        return 0;
    }

    private static void Check(Type player, Type comet, Type winter)
    {
        var movement = AccessTools.Method(comet, "MovementAudioTranspiler");
        var observer = AccessTools.Method(comet, "MovementAudioPlayed");
        var snow = AccessTools.Method(winter, "Transpiler");
        var surface = AccessTools.Method(winter, "PlaySurface");
        var weather = AccessTools.Method(winter, "PlayWeather");
        var targets = new[] { AccessTools.Method(player, "DefaultPlay"), AccessTools.Method(player, "PlayStepSound"),
            AccessTools.EnumeratorMoveNext(AccessTools.Method(player, "Sprint")) };
        foreach (var target in targets)
        {
            List<CodeInstruction> WithComet(IEnumerable<CodeInstruction> input) =>
                ((IEnumerable<CodeInstruction>)movement.Invoke(null, new object[] { input, target })).ToList();
            var vanilla = WithComet(PatchProcessor.GetOriginalInstructions(target));
            var expectedInputs = HearingInputs(vanilla);
            foreach (bool winterFirst in new[] { false, true })
            {
                ILGenerator il;
                var code = PatchProcessor.GetOriginalInstructions(target, out il);
                List<CodeInstruction> WithSnow(IEnumerable<CodeInstruction> input) =>
                    ((IEnumerable<CodeInstruction>)snow.Invoke(null, new object[] { input, il, target })).ToList();
                code = winterFirst ? WithComet(WithSnow(code)) : WithSnow(WithComet(code));
                int surfaceAt = code.IndexOf(code.Single(i => i.Calls(surface)));
                int weatherAt = code.IndexOf(code.Single(i => i.Calls(weather)));
                int observerAt = code.IndexOf(code.Single(i => i.Calls(observer)));
                if (surfaceAt >= weatherAt || observerAt != weatherAt + 4 || !expectedInputs.SequenceEqual(HearingInputs(code)))
                    throw new InvalidOperationException("Expected snow surface, snow weather, then COMET hearing on " + target);
            }
            Console.WriteLine("Both patch orders passed: " + target.DeclaringType + "." + target.Name);

            (OpCode, object)[] HearingInputs(List<CodeInstruction> code)
            {
                int end = code.IndexOf(code.Single(i => i.Calls(observer)));
                return code.GetRange(end - 3, 3).Select(i =>
                    (i.opcode, i.operand is LocalBuilder local ? (object)local.LocalIndex : i.operand)).ToArray();
            }
        }
    }
}
