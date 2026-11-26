using System.Reflection.Emit;
using HarmonyLib;

namespace Comet.IntegrationProbe;

internal static class ReloadDecisionAudit
{
    internal static void ExecuteBranch(CodeInstruction[] rewritten, int probeCall)
    {
        // Run the rewritten section from the shipped SAIN decision. Substitute
        // the probe outcome as input and a sentinel for the native fallback.
        var method = new DynamicMethod("sainReloadBranch", typeof(int), new[] { typeof(int) });
        var il = method.GetILGenerator();
        var outcome = il.DeclareLocal(typeof(int));
        var fallback = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        for (int j = probeCall + 1; j <= probeCall + 7; j++)
        {
            var op = rewritten[j].opcode;
            if (op == OpCodes.Ldloc || op == OpCodes.Stloc) il.Emit(op, outcome);
            else if (op.FlowControl == FlowControl.Cond_Branch) il.Emit(op, fallback);
            else il.Emit(op);
        }
        il.MarkLabel(fallback);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Ret);
        var run = method.CreateDelegate<Func<int, int>>();
        if (run(-1) != 0 || run(0) != 2 || run(1) != 1)
            throw new Exception("SAIN hold, native failure and successful reload must follow distinct branches");
    }
}
