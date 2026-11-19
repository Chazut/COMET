using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace Comet.IntegrationProbe;

// Reads actual EFT/SAIN IL. No game methods, Unity calls or detours are executed.
// Production transpilers are extracted by IntegrationAudit with reflection-only AccessTools.
static class TranspilerAudit
{
    static readonly Dictionary<short, OpCode> Opcodes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(OpCode))
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

    public static void Check(MethodBase target, MethodInfo transpiler)
    {
        foreach (var variant in new[] { "original", "long branches", "long branches and locals" })
            CheckVariant(target, transpiler, variant);
        if (transpiler.Name == "PartialBarrelReload")
            BarrelReloadAudit.Check(transpiler, Read(target));
        if (transpiler.Name == "FloorRangeTranspiler")
            FloorRangeAudit.Check(transpiler, Read(target));
    }

    static void CheckVariant(MethodBase target, MethodInfo transpiler, string variant)
    {
        var input = Read(target);
        foreach (var instruction in input)
        {
            // Harmony's Mono pipeline widens branches before our transpiler runs.
            // Also exercise equivalent wide local operands used by other IL pipelines.
            if (
                (
                    variant != "original"
                    && instruction.opcode.OperandType == OperandType.ShortInlineBrTarget
                )
                || (
                    variant == "long branches and locals"
                    && instruction.opcode.OperandType == OperandType.ShortInlineVar
                )
            )
                instruction.opcode = Opcodes.Values.Single(o =>
                    o.Name == instruction.opcode.Name![..^2]
                );
        }
        var before = input.Select(i => new CodeInstruction(i)).ToArray();
        var args = transpiler
            .GetParameters()
            .Select(p => p.Name == "__originalMethod" ? (object)target : input)
            .ToArray();
        CodeInstruction[] after;
        try
        {
            after = ((IEnumerable<CodeInstruction>)transpiler.Invoke(null, args)!).ToArray();
        }
        catch (Exception e)
        {
            throw new InvalidOperationException(
                $"{transpiler.Name} ({variant}) on {target.DeclaringType}.{target.Name}: {e.GetBaseException().Message}",
                e
            );
        }
        foreach (var original in before)
        {
            var label = original.labels.Single();
            if (after.Count(i => i.labels.Contains(label)) != 1)
                throw new Exception($"{transpiler.Name}: lost/duplicated instruction label");
            if (original.operand is MonoGrenadeToken token)
            {
                var same = after.Single(i => i.labels.Contains(label));
                if (same.opcode != original.opcode || !Equals(same.operand, token))
                    throw new Exception("Unrelated native grenade event instruction was modified");
            }
            if (
                original.operand is not MethodInfo call
                || original.opcode != OpCodes.Call && original.opcode != OpCodes.Callvirt
            )
                continue;
            int start = Array.FindIndex(after, i => i.labels.Contains(label)),
                end = start + 1;
            while (end < after.Length && after[end].labels.Count == 0)
                end++;
            var segment = after[start..end];
            if (
                !segment.Any(i =>
                    i.operand is MethodInfo m && m.DeclaringType == typeof(Comet.Client.Plugin)
                )
            )
                continue;
            // Check the actual receiver type too, not only a method name/call count.
            // This catches routing an interface call to a wrapper requiring a concrete implementation.
            var stack = new List<Type>();
            if (!call.IsStatic)
                stack.Add(call.DeclaringType!);
            stack.AddRange(call.GetParameters().Select(p => p.ParameterType));
            foreach (var i in segment)
            {
                if (i.opcode == OpCodes.Ldarg_0)
                    stack.Add(target.DeclaringType!);
                else if (i.opcode == OpCodes.Ldfld && i.operand is FieldInfo field)
                {
                    Pop(field.DeclaringType!);
                    stack.Add(field.FieldType);
                }
                else if (i.opcode == OpCodes.Call && i.operand is MethodInfo wrapper)
                {
                    foreach (var p in wrapper.GetParameters().Reverse())
                        Pop(p.ParameterType);
                    if (!wrapper.IsStatic)
                        Pop(wrapper.DeclaringType!);
                    if (wrapper.ReturnType != typeof(void))
                        stack.Add(wrapper.ReturnType);
                }
                else
                    throw new Exception(
                        $"{transpiler.Name}: unvalidated replacement opcode {i.opcode}"
                    );
            }
            var expected =
                call.ReturnType == typeof(void) ? Array.Empty<Type>() : new[] { call.ReturnType };
            if (!stack.SequenceEqual(expected))
                throw new Exception($"{transpiler.Name}: changed return stack for {call}");
            void Pop(Type expectedType)
            {
                if (stack.Count == 0)
                    throw new Exception($"{transpiler.Name}: replacement stack underflow");
                var actual = stack[^1];
                stack.RemoveAt(stack.Count - 1);
                if (!expectedType.IsAssignableFrom(actual))
                    throw new Exception($"{transpiler.Name}: {actual} cannot bind {expectedType}");
            }
        }
        Console.WriteLine(
            $"  IL OK ({variant}): {transpiler.Name} -> {target.DeclaringType}.{target.Name}"
        );
        if (transpiler.Name == "RouteQuickReload")
        {
            var reload = before.Single(i => i.operand is MethodInfo m && m.Name == "ReloadMag");
            Reject(Array.Empty<CodeInstruction>(), "missing call");
            Reject(
                new[] { new CodeInstruction(reload), new CodeInstruction(reload) },
                "duplicate call"
            );
            var wrong = new CodeInstruction(reload)
            {
                operand = typeof(EFT.Player.FirearmController).GetMethod(
                    "ReloadMag",
                    new[]
                    {
                        typeof(EFT.InventoryLogic.Magazine),
                        typeof(EFT.InventoryLogic.ItemAddress),
                        typeof(Comfort.Common.Callback),
                    }
                )!,
            };
            Reject(new[] { wrong }, "same name but wrong declaring type");
            void Reject(CodeInstruction[] code, string scenario)
            {
                bool rejected = false;
                try
                {
                    (
                        (IEnumerable<CodeInstruction>)
                            transpiler.Invoke(null, new object[] { code })!
                    ).ToArray();
                }
                catch (Exception e) when (e.GetBaseException() is InvalidOperationException)
                {
                    rejected = true;
                }
                if (!rejected)
                    throw new Exception("W04 accepted " + scenario);
            }
            Console.WriteLine("  W04: missing, duplicate and wrong-owner calls rejected.");
        }
    }

    private sealed record MonoGrenadeToken(int Token);

    static List<CodeInstruction> Read(MethodBase method)
    {
        var bytes = method.GetMethodBody()?.GetILAsByteArray()
            ?? throw new Exception("No method body: " + method);
        using var reader = new BinaryReader(new MemoryStream(bytes));
        var result = new List<CodeInstruction>();
        var module = method.Module;
        var offsets = new List<int>();
        var ends = new List<int>();
        var types = method.DeclaringType?.GetGenericArguments();
        var methods = method.IsGenericMethod ? method.GetGenericArguments() : null;
        while (reader.BaseStream.Position < bytes.Length)
        {
            offsets.Add((int)reader.BaseStream.Position);
            byte b = reader.ReadByte();
            short key = b == 0xfe ? (short)(0xfe00 | reader.ReadByte()) : b;
            var op = Opcodes[key];
            object? operand = op.OperandType switch
            {
                OperandType.InlineNone => null,
                OperandType.ShortInlineI => (int)reader.ReadSByte(),
                OperandType.InlineI => reader.ReadInt32(),
                OperandType.InlineI8 => reader.ReadInt64(),
                OperandType.ShortInlineR => reader.ReadSingle(),
                OperandType.InlineR => reader.ReadDouble(),
                OperandType.ShortInlineVar => (int)reader.ReadByte(),
                OperandType.InlineVar => (int)reader.ReadUInt16(),
                OperandType.ShortInlineBrTarget => (int)reader.ReadSByte(),
                OperandType.InlineBrTarget => reader.ReadInt32(),
                OperandType.InlineSwitch => Enumerable
                    .Range(0, reader.ReadInt32())
                    .Select(_ => reader.ReadInt32())
                    .ToArray(),
                OperandType.InlineString => module.ResolveString(reader.ReadInt32()),
                OperandType.InlineSig => module.ResolveSignature(reader.ReadInt32()),
                OperandType.InlineMethod
                or OperandType.InlineField
                or OperandType.InlineType
                or OperandType.InlineTok => Resolve(reader.ReadInt32()),
                _ => throw new NotSupportedException(op.OperandType.ToString()),
            };
            result.Add(new CodeInstruction(op, operand));
            ends.Add((int)reader.BaseStream.Position);
        }
        // Match Harmony operands: branch destinations are Labels, not raw byte offsets.
        var generator = new DynamicMethod("labels", typeof(void), Type.EmptyTypes).GetILGenerator();
        var labels = offsets.ToDictionary(o => o, _ => generator.DefineLabel());
        for (int i = 0; i < result.Count; i++)
        {
            var instruction = result[i];
            instruction.labels.Add(labels[offsets[i]]);
            if (
                instruction.opcode.OperandType
                is OperandType.ShortInlineBrTarget
                    or OperandType.InlineBrTarget
            )
                instruction.operand = labels[ends[i] + (int)instruction.operand];
            else if (instruction.opcode.OperandType == OperandType.InlineSwitch)
                instruction.operand = ((int[])instruction.operand)
                    .Select(delta => labels[ends[i] + delta])
                    .ToArray();
        }
        return result;

        object? Resolve(int token)
        {
            try
            {
                return module.ResolveMember(token, types, methods);
            }
            catch (TypeLoadException e)
                when (method.DeclaringType == typeof(SAIN.SAINComponent.Classes.Talk.EnemyTalk)
                    && method.Name == "Dispose"
                    && e.Message.Contains("'GDelegate17'")
                    && e.Message.Contains("must be sealed")
                )
            {
                // CoreCLR rejects this EFT grenade delegate. Preserve the exact token and
                // opcode so the assertion above still checks this unrelated event teardown.
                return new MonoGrenadeToken(token);
            }
        }
    }
}
