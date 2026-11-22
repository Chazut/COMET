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
        if (transpiler.Name == "RouteQuickReload")
        {
            var frame = typeof(Comet.Client.Plugin).GetMethod("QuickReloadFrameFor", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var calls = Read(frame).Select(i => i.operand).OfType<MethodBase>().ToArray();
            if (!calls.Any(m => m.Name == "CanAccept") || calls.Any(m => m.Name == "IsAmmoCompatible"))
                throw new Exception("Quick reload spares must count mountable magazines irrespective of their cartridges.");
        }
    }

    public static void CheckPistolSwitchTimer()
    {
        var request = typeof(Comet.Client.Plugin).GetMethod("TrySourcePistol", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var code = Read(request);
        var timer = typeof(BotWeaponSelector).GetField("_nextChangeTime", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        int reset = code.FindIndex(i => i.StoresField(timer));
        int change = code.FindIndex(i => i.operand is MethodInfo m
            && m.DeclaringType == typeof(BotWeaponSelector) && m.Name == nameof(BotWeaponSelector.TryChangeToSlot));
        if (reset < 1 || change <= reset || code.FindLastIndex(i => i.StoresField(timer)) != reset
            || !code[reset - 1].LoadsConstant(0f))
            throw new Exception("W02 must clear the selector timer before the request and leave the native outcome intact.");
    }

    static void CheckCloseFightRetarget(MethodBase target, CodeInstruction[] before, CodeInstruction[] after)
    {
        var hook = typeof(Comet.Client.Plugin).GetMethod("RecheckDogFightTarget", BindingFlags.Static | BindingFlags.NonPublic)!;
        int call = Array.FindIndex(after, i => i.Calls(hook));
        if (call < 8 || call + 5 >= after.Length || after.Length != before.Length + 5)
            throw new Exception("Missing dogfight retarget insertion.");
        var held = target.DeclaringType!.GetField("_lastDogFightTarget", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var eligible = target.DeclaringType.GetMethod("ShallDogfightEnemy", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var branch = after[call - 5];
        if (!after[call - 7].LoadsField(held) || !after[call - 6].Calls(eligible)
            || (branch.opcode != OpCodes.Brfalse && branch.opcode != OpCodes.Brfalse_S)
            || !after[call - 4].IsLdarg(0) || !after[call - 3].IsLdarg(1) || !after[call - 2].IsLdarg(0)
            || after[call - 1].opcode != OpCodes.Ldflda || !Equals(after[call - 1].operand, held)
            || !after[call + 1].IsLdarg(2) || !after[call + 2].IsLdarg(0) || !after[call + 3].LoadsField(held)
            || after[call + 4].opcode != OpCodes.Stind_Ref)
            throw new Exception("Only a retained eligible dogfight target can invoke the closer-target scan.");
        var pars = hook.GetParameters();
        if (hook.ReturnType != typeof(void) || pars.Length != 3 || pars[0].ParameterType != target.DeclaringType
            || pars[1].ParameterType != target.GetParameters()[0].ParameterType || pars[2].ParameterType != held.FieldType.MakeByRefType())
            throw new Exception("The dogfight hook must write back the native held enemy.");
        var original = after.Take(call - 4).Concat(after.Skip(call + 1));
        if (!original.Zip(before).All(p => p.First.opcode == p.Second.opcode && Equals(p.First.operand, p.Second.operand)
            && p.First.labels.SequenceEqual(p.Second.labels)) ||
            after.Skip(call - 4).Take(5).Any(i => i.labels.Count != 0))
            throw new Exception("Refusals and first acquisition must keep their native instructions and branch targets.");
    }

    static void CheckCloseFightScan(MethodBase target, CodeInstruction[] before, CodeInstruction[] after)
    {
        var hook = typeof(Comet.Client.Plugin).GetMethod("CloseFightScanInterval", BindingFlags.Static | BindingFlags.NonPublic)!;
        int call = Array.FindIndex(after, i => i.Calls(hook));
        var deadline = target.DeclaringType!.GetField("_changeDFTargetTime", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var parameters = hook.GetParameters();
        if (call < 5 || call + 2 >= after.Length || after.Count(i => i.Calls(hook)) != 1
            || !after[call - 3].Calls(typeof(UnityEngine.Time).GetProperty("time")!.GetMethod!)
            || !after[call - 2].LoadsConstant(0.5f) || !after[call - 1].IsLdarg(0)
            || after[call + 1].opcode != OpCodes.Add || !after[call + 2].StoresField(deadline)
            || after[call - 1].labels.Count != 0 || after[call].labels.Count != 0
            || parameters.Length != 2 || parameters[0].ParameterType != typeof(float)
            || parameters[1].ParameterType != target.DeclaringType || hook.ReturnType != typeof(float))
            throw new Exception("The close-fight interval must feed only the native scan's next deadline.");
        // The existing test verifies every original instruction and label after both insertions are removed.
        CheckCloseFightRetarget(target, before, after.Take(call - 1).Concat(after.Skip(call + 1)).ToArray());
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
        if (transpiler.Name == "RouteCloseFightChecks")
            CheckCloseFightScan(target, before, after);
        if (transpiler.Name == "MovementAudioTranspiler")
        {
            CheckMovementPlayback(target, before, after);
            Console.WriteLine($"  IL OK ({variant}): movement hearing preserves playback on {target.Name}");
            return;
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
            if (transpiler.Name == "InsertWalkingGaze")
            {
                if (call.Name != "ResetSuppressing" || call.ReturnType != typeof(void)
                    || segment.Length != 5 || !Equals(segment[0].operand, call)
                    || segment[0].opcode != original.opcode || segment[1].opcode != OpCodes.Ldarg_0
                    || segment[2].opcode != OpCodes.Call || segment[2].operand is not MethodInfo gaze
                    || gaze.DeclaringType != typeof(Comet.Client.Plugin) || gaze.Name != "FaceWalkWithoutThreat"
                    || gaze.ReturnType != typeof(bool) || !gaze.IsStatic
                    || !gaze.GetParameters().Single().ParameterType.IsAssignableFrom(target.DeclaringType!)
                    || segment[3].opcode != OpCodes.Brfalse || segment[4].opcode != OpCodes.Ret
                    || !before.Zip(before.Skip(1)).Any(pair => pair.First.operand is MethodInfo m
                        && m.Name == "TrySuppressAnyEnemy" && (pair.Second.opcode == OpCodes.Brtrue
                            || pair.Second.opcode == OpCodes.Brtrue_S) && Equals(pair.Second.operand, segment[3].operand)))
                    throw new Exception("Walking gaze must follow a failed suppression and rejoin native steering");
                continue;
            }
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

    // The no-cover postfix relies on SeekCover being the unique terminal default.
    public static void CheckNativeCoverFallback()
    {
        var decision = typeof(SAIN.SAINComponent.Classes.Decision.EnemyDecisionClass).GetMethod("GetDecision")!;
        var code = Read(decision);
        int seek = (int)SAIN.Preset.Shared.Enums.ECombatDecision.SeekCover;
        var stores = Enumerable.Range(1, code.Count - 2).Where(i =>
            code[i - 1].opcode == OpCodes.Ldarg_1 && code[i].LoadsConstant(seek) && code[i + 1].opcode == OpCodes.Stind_I4);
        if (!stores.SequenceEqual(new[] { code.Count - 4 }) || code[^2].opcode != OpCodes.Ldc_I4_1 || code[^1].opcode != OpCodes.Ret)
            throw new Exception("Official SAIN no longer returns SeekCover solely as the final default.");
    }

    static void CheckMovementPlayback(MethodBase target, CodeInstruction[] before, CodeInstruction[] after)
    {
        var observer = after.Single(i => i.operand is MethodInfo { Name: "MovementAudioPlayed" } m &&
            m.DeclaringType == typeof(Comet.Client.Plugin));
        var calls = Read((MethodInfo)observer.operand).Select(i => i.operand).OfType<MethodInfo>().ToArray();
        if (calls.Count(m => m.Name == "CaptureMovement") != 1 || calls.Any(m => m.Name == "Play"))
            throw new Exception("The movement observer must report the sound once without playing it again.");
        int end = Array.IndexOf(after, observer);
        int play = end - 4;
        var weather = before.Single(i => i.operand is MethodInfo m && m.Name == "Play" &&
            m.DeclaringType == typeof(Audio.AudioWeatherSystem.IPlayerStepAudioController));
        if (play < 0 || !Equals(after[play].operand, weather.operand))
            throw new Exception("The COMET observer must immediately follow the original weather playback.");

        string[] expected = target.Name switch
        {
            "DefaultPlay" => ["ldarg.0", "ldarg.3", "ldarg.2"],
            "PlayStepSound" => ["ldarg.0", "ldloc.1", "ldloc 7"],
            "MoveNext" => ["ldloc.1", "ldc.i4.2", "ldloc 7"],
            _ => throw new Exception("Unexpected movement target: " + target)
        };
        var loads = after[(play + 1)..end];
        var actual = loads.Select(i => i.opcode == OpCodes.Ldloc || i.opcode == OpCodes.Ldloc_S
            ? "ldloc " + i.operand : i.opcode.Name);
        if (!expected.SequenceEqual(actual) || after[(play + 1)..(end + 1)].Any(i => i.labels.Count != 0 || i.blocks.Count != 0))
            throw new Exception("Wrong player, movement, volume or alternate entry point on " + target);

        var preserved = after.Take(play + 1).Concat(after.Skip(end + 1)).ToArray();
        if (preserved.Length != before.Length || !before.Zip(preserved).All(pair =>
            pair.First.opcode == pair.Second.opcode && Equals(pair.First.operand, pair.Second.operand) &&
            pair.First.labels.SequenceEqual(pair.Second.labels) && pair.First.blocks.SequenceEqual(pair.Second.blocks)))
            throw new Exception("COMET changed EFT playback or its control flow on " + target);
    }

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
