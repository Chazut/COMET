using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

// Cleanup now uses independent delegates. Follow same-assembly calls and delegate
// targets instead of requiring every resource operation in one monolithic method.
internal static class CleanupIl
{
    internal static HashSet<string> Calls(PEReader pe,MetadataReader md,string type,string method)
    {
        var root=md.TypeDefinitions.Select(md.GetTypeDefinition).Single(x=>md.GetString(x.Name)==type).GetMethods()
            .Single(h=>md.GetString(md.GetMethodDefinition(h).Name)==method);
        var queue=new Queue<MethodDefinitionHandle>();queue.Enqueue(root);
        var seen=new HashSet<MethodDefinitionHandle>();var calls=new HashSet<string>();
        while(queue.Count>0)
        {
            var handle=queue.Dequeue();if(!seen.Add(handle))continue;
            var def=md.GetMethodDefinition(handle);if(def.RelativeVirtualAddress==0)continue;
            foreach(var instruction in FloorHearingTests.ReadIl(pe.GetMethodBody(def.RelativeVirtualAddress).GetILBytes()!))
            {
                if(instruction.op!=OpCodes.Call&&instruction.op!=OpCodes.Callvirt&&instruction.op!=OpCodes.Ldftn&&instruction.op!=OpCodes.Ldvirtftn)continue;
                int token=(int)instruction.operand!;calls.Add(FloorHearingTests.Name(md,token));
                var callee=MetadataTokens.EntityHandle(token);
                if(callee.Kind==HandleKind.MethodSpecification)callee=md.GetMethodSpecification((MethodSpecificationHandle)callee).Method;
                if(callee.Kind==HandleKind.MethodDefinition)queue.Enqueue((MethodDefinitionHandle)callee);
            }
        }
        return calls;
    }
}
