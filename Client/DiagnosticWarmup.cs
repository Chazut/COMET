using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Serialization;

namespace Comet.Client;

// Prime metadata during plugin loading, before any raid. No diagnostic producer,
// payload constructor, property getter, bot callback or Unity API is executed.
internal static class DiagnosticWarmup
{
    internal sealed class Report
    {
        internal int Contracts, Methods, Failed;
        internal double Milliseconds;
        internal readonly List<string> Unavailable = new();
        internal void Failure(string source, Exception ex)
        {
            Failed++;
            if(Unavailable.Count<8) Unavailable.Add(source + ": " + ex.GetType().Name + ": " + ex.Message);
        }
    }
    private static readonly Dictionary<short, OpCode> Codes = BuildCodes();
    private static Dictionary<short, OpCode> BuildCodes()
    {
        var result = new Dictionary<short, OpCode>();
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (field.GetValue(null) is OpCode code) result[code.Value] = code;
        return result;
    }
    internal static Report Prepare(Assembly assembly, IContractResolver resolver, bool prepareMethods = true)
    {
        var clock = Stopwatch.StartNew(); var report = new Report();
        var pending = new Queue<Type>(); var visited = new HashSet<Type>();
        void Add(Type? type)
        {
            if (type == null || type.ContainsGenericParameters || type.IsPointer || type.IsByRef
                || typeof(Delegate).IsAssignableFrom(type) || !visited.Add(type)) return;
            // Payloads consist of COMET value data and framework collections only.
            // Native game objects are never traversed, even if a future event includes one.
            if (type.Assembly == assembly || type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true)
                pending.Enqueue(type);
        }
        foreach (var type in assembly.GetTypes())
        {
            if (type.Namespace == "Comet.Core") Add(type);
            if (type.ContainsGenericParameters) continue;
            // First combat use also pays for policy/adapter constructors. Prepare
            // their code here without running initializers or touching Unity.
            if(prepareMethods && (type.Namespace=="Comet.Client" || type.Namespace=="Comet.Core"))
                foreach(var constructor in type.GetConstructors(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance))
                    try { RuntimeHelpers.PrepareMethod(constructor.MethodHandle);report.Methods++; }
                    catch(Exception ex) { report.Failure(type.FullName+".ctor",ex); }
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
                | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (method.ContainsGenericParameters) continue;
                try
                {
                    foreach (var created in ConstructedTypes(method)) Add(created);
                    if (prepareMethods && (type.Namespace == "Comet.Client" || type.Namespace == "Comet.Core")
                        && !method.IsAbstract && method.GetMethodBody() != null)
                    {
                        RuntimeHelpers.PrepareMethod(method.MethodHandle);
                        report.Methods++;
                    }
                }
                catch (Exception ex) { report.Failure(type.FullName + "." + method.Name, ex); }
            }
        }
        // Recursive value contracts include the closed anonymous envelope types
        // referenced by newobj in the compiled producers, not just open definitions.
        while (pending.Count > 0)
        {
            var type = pending.Dequeue();
            try
            {
                var contract = resolver.ResolveContract(type); report.Contracts++;
                if (contract is JsonObjectContract obj)
                    foreach (var property in obj.Properties) if (!property.Ignored) Add(property.PropertyType);
                if (contract is JsonArrayContract array) Add(array.CollectionItemType);
                if (contract is JsonDictionaryContract dictionary)
                { Add(dictionary.DictionaryKeyType); Add(dictionary.DictionaryValueType); }
            }
            catch (Exception ex) { report.Failure(type.FullName ?? type.Name, ex); }
        }
        report.Milliseconds = clock.Elapsed.TotalMilliseconds; return report;
    }
    private static IEnumerable<Type> ConstructedTypes(MethodInfo method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray(); if (il == null) yield break;
        int at = 0;
        while (at < il.Length)
        {
            short code = il[at++]; if (code == 0xfe) code = (short)(0xfe00 | il[at++]);
            var op = Codes[code];
            if (op == OpCodes.Newobj)
            {
                var constructor = method.Module.ResolveMethod(BitConverter.ToInt32(il, at));
                var type = constructor?.DeclaringType;
                if (type != null && type.Assembly == method.Module.Assembly
                    && (type.Namespace == "Comet.Core" || type.IsDefined(typeof(CompilerGeneratedAttribute), false)
                        && type.Name.Contains("AnonymousType"))) yield return type;
            }
            at += op.OperandType switch {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, at),
                _ => 4
            };
        }
    }
}
