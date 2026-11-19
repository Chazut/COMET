using System.Reflection;
using System.Reflection.Emit;
using EFT.InventoryLogic;
using HarmonyLib;

namespace Comet.IntegrationProbe;

static class TranspilerContracts
{
    internal static List<CodeInstruction> Copy(IEnumerable<CodeInstruction> code) =>
        code.Select(i => new CodeInstruction(i)).ToList();

    internal static CodeInstruction[] Apply(
        MethodInfo transpiler,
        IEnumerable<CodeInstruction> code
    ) => ((IEnumerable<CodeInstruction>)transpiler.Invoke(null, new object[] { code })!).ToArray();

    internal static void Reject(
        MethodInfo transpiler,
        IEnumerable<CodeInstruction> code,
        string scenario
    )
    {
        try
        {
            Apply(transpiler, code);
        }
        catch (Exception e) when (e.GetBaseException() is InvalidOperationException)
        {
            return;
        }
        throw new Exception(transpiler.Name + " accepted incompatible IL: " + scenario);
    }
}

static class BarrelReloadAudit
{
    internal static void Check(MethodInfo transpiler, List<CodeInstruction> source)
    {
        var getter = typeof(Weapon).GetProperty(nameof(Weapon.ChamberAmmoCount))!.GetMethod!;
        int index = source.FindIndex(i => i.Calls(getter)),
            checks = 0;
        var wrapper = typeof(Comet.Client.Plugin).GetMethod(
            "BarrelReloadChamberPredicate",
            BindingFlags.NonPublic | BindingFlags.Static
        )!;
        foreach (var conditional in new[] { OpCodes.Brfalse_S, OpCodes.Brfalse })
        foreach (var unconditional in new[] { OpCodes.Br_S, OpCodes.Br })
        {
            var input = TranspilerContracts.Copy(source);
            input[index + 1].opcode = conditional;
            input[index + 3].opcode = unconditional;
            input[index].blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock));
            var before = TranspilerContracts.Copy(input);
            var output = TranspilerContracts.Apply(transpiler, input);
            if (output.Length != before.Count)
                throw new Exception("W01 changed native instruction count");
            for (int i = 0; i < output.Length; i++)
            {
                if (
                    !output[i].labels.SequenceEqual(before[i].labels)
                    || !output[i].blocks.SequenceEqual(before[i].blocks)
                )
                    throw new Exception("W01 changed labels or exception boundaries");
                if (i == index)
                {
                    if (output[i].opcode != OpCodes.Call || !Equals(output[i].operand, wrapper))
                        throw new Exception("W01 did not replace the getter");
                }
                else if (
                    output[i].opcode != before[i].opcode
                    || !Equals(output[i].operand, before[i].operand)
                )
                    throw new Exception("W01 changed another instruction or branch destination");
            }
            checks++;
        }
        void Reject(string scenario, Action<List<CodeInstruction>> change)
        {
            var code = TranspilerContracts.Copy(source);
            change(code);
            TranspilerContracts.Reject(transpiler, code, scenario);
            checks++;
        }
        Reject("missing getter", c => c[index] = new CodeInstruction(OpCodes.Nop));
        Reject("duplicate getter", c => c.Add(new CodeInstruction(c[index])));
        Reject("wrong getter owner", c =>
            c[index].operand = typeof(string).GetProperty(nameof(string.Length))!.GetMethod!
        );
        Reject("inverted conditional", c => c[index + 1].opcode = OpCodes.Brtrue);
        Reject("non-branch conditional", c => c[index + 1].opcode = OpCodes.Nop);
        Reject("wrong first constant", c => c[index + 2].opcode = OpCodes.Ldc_I4_1);
        Reject("conditional instead of unconditional jump", c =>
            c[index + 3].opcode = OpCodes.Brfalse
        );
        Reject("wrong second constant", c => c[index + 4].opcode = OpCodes.Ldc_I4_0);
        Reject("truncated predicate", c => c.RemoveRange(index + 4, c.Count - index - 4));
        TranspilerContracts.Reject(transpiler, Array.Empty<CodeInstruction>(), "empty method");
        checks++;
        Console.WriteLine(
            $"  W01: {checks} branch-form and rejection cases passed; only the getter changes, labels/blocks/targets preserved."
        );
    }
}

static class FloorRangeAudit
{
    internal static void Check(MethodInfo transpiler, List<CodeInstruction> source)
    {
        int index = source.FindIndex(i => i.opcode == OpCodes.Stloc_S && Equals(i.operand, 4)),
            checks = 0;
        var generator = new DynamicMethod("locals", typeof(void), Type.EmptyTypes).GetILGenerator();
        var locals = Enumerable
            .Range(0, 5)
            .Select(_ => generator.DeclareLocal(typeof(float)))
            .ToArray();
        foreach (var store in new[] { OpCodes.Stloc_S, OpCodes.Stloc })
        foreach (object operand in new object[] { 4, locals[4] })
        {
            var input = TranspilerContracts.Copy(source);
            input[index].opcode = store;
            input[index].operand = operand;
            input[index].blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock));
            var output = TranspilerContracts.Apply(transpiler, input);
            int replacement = Array.FindIndex(output, i =>
                i.labels.Contains(source[index].labels.Single())
            );
            if (
                output.Length != source.Count + 5
                || replacement < 0
                || output[replacement].opcode != OpCodes.Ldarg_0
                || !output[replacement].blocks.SequenceEqual(input[index].blocks)
                || output[replacement + 5].opcode != store
                || !Equals(output[replacement + 5].operand, operand)
            )
                throw new Exception("P04 lost the range store or its metadata");
            checks++;
        }
        void Reject(string scenario, Action<List<CodeInstruction>> change)
        {
            var code = TranspilerContracts.Copy(source);
            change(code);
            TranspilerContracts.Reject(transpiler, code, scenario);
            checks++;
        }
        Reject("wrong range local", c => c[index].operand = 5);
        Reject("wrong range arithmetic", c => c[index - 1].opcode = OpCodes.Add);
        Reject("wrong sound argument", c => c[index + 1].opcode = OpCodes.Ldarg_2);
        Reject("missing distance field", c => c[index + 2] = new CodeInstruction(OpCodes.Nop));
        Reject("truncated range", c => c.RemoveRange(index + 2, c.Count - index - 2));
        int environment = source.FindIndex(i =>
            i.operand is MethodInfo m && m.Name == "CalcEnvironmentMod"
        );
        Reject("wrong modifier local", c => c[environment + 1].opcode = OpCodes.Stloc_1);
        int occlusion = source.FindIndex(i =>
            i.operand is MethodInfo m && m.Name == "CalcOcclusionMod"
        );
        Reject("truncated modifier", c => c.RemoveRange(occlusion + 1, c.Count - occlusion - 1));
        TranspilerContracts.Reject(transpiler, Array.Empty<CodeInstruction>(), "empty method");
        checks++;
        Console.WriteLine($"  P04: {checks} local-form and rejection cases passed.");
    }
}
