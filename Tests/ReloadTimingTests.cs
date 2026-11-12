using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
static class ReloadTimingTests { public static int Run()=>SourceWeaponBaselineTests.Run("W01");
    internal static int Calls(PEReader pe,MetadataReader md,MethodDefinition method,string name)
    {
        var ops=typeof(OpCodes).GetFields(BindingFlags.Public|BindingFlags.Static).Where(f=>f.FieldType==typeof(OpCode)).Select(f=>(OpCode)f.GetValue(null)!).ToDictionary(o=>unchecked((ushort)o.Value));
        var il=pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!;int count=0;
        for(int i=0;i<il.Length;){ushort v=il[i++];if(v==0xfe)v=(ushort)(0xfe00|il[i++]);var op=ops[v];
            if(op==OpCodes.Call||op==OpCodes.Callvirt){var h=MetadataTokens.EntityHandle(BitConverter.ToInt32(il,i));string called=h.Kind==HandleKind.MethodDefinition?md.GetString(md.GetMethodDefinition((MethodDefinitionHandle)h).Name):h.Kind==HandleKind.MemberReference?md.GetString(md.GetMemberReference((MemberReferenceHandle)h).Name):"";if(called==name)count++;}
            i+=op.OperandType switch{OperandType.InlineNone=>0,OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar=>1,OperandType.InlineVar=>2,OperandType.InlineI8 or OperandType.InlineR=>8,OperandType.InlineSwitch=>4+4*BitConverter.ToInt32(il,i),_=>4};}
        return count;
    }
}
