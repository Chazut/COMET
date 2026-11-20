using System.Reflection;

namespace Comet.IntegrationProbe;

// Reflection-only equivalent of the AccessTools lookups used by registration. The game's older
// Harmony runtime cannot initialize on .NET 10; it is not used to install detours in this harness.
public static class ProbeAccess
{
    public delegate ref F FieldRef<T, F>(T instance);

    public static FieldRef<T, F> FieldRefAccess<T, F>(string name)
    {
        var field = Field(typeof(T), name);
        if (field == null || field.FieldType != typeof(F))
            throw new MissingFieldException(typeof(T).FullName + "." + name);
        return (T instance) =>
            throw new InvalidOperationException(
                "Field access is not executed by the registration audit."
            );
    }

    public static T MethodDelegate<T>(MethodInfo method)
        where T : Delegate => (T)Delegate.CreateDelegate(typeof(T), method);

    public static MethodInfo? PropertyGetter(Type type, string name) =>
        Property(type, name)?.GetGetMethod(true);

    public static MethodInfo? PropertySetter(Type type, string name) =>
        Property(type, name)?.GetSetMethod(true);

    public static MethodInfo? EnumeratorMoveNext(MethodInfo method)
    {
        var iterator = method
            .GetCustomAttribute<System.Runtime.CompilerServices.IteratorStateMachineAttribute>()
            ?.StateMachineType;
        return iterator == null
            ? throw new MissingMethodException("No iterator metadata: " + method)
            : Method(iterator, "MoveNext");
    }

    const BindingFlags Flags =
        BindingFlags.Public
        | BindingFlags.NonPublic
        | BindingFlags.Static
        | BindingFlags.Instance
        | BindingFlags.DeclaredOnly;

    public static ConstructorInfo? Constructor(
        Type type,
        Type[]? parameters = null,
        bool searchForStatic = false
    ) =>
        type.GetConstructor(
            Flags & ~(searchForStatic ? BindingFlags.Instance : BindingFlags.Static),
            null,
            parameters ?? Type.EmptyTypes,
            null
        );

    public static MethodInfo? DeclaredMethod(Type type, string name) => type.GetMethod(name, Flags);

    public static MethodInfo? Method(
        Type? type,
        string name,
        Type[]? parameters = null,
        Type[]? generics = null
    )
    {
        for (var t = type; t != null; t = t.BaseType)
        {
            var methods = t.GetMethods(Flags)
                .Where(m =>
                    m.Name == name
                    && (
                        parameters == null
                        || m.GetParameters().Select(p => p.ParameterType).SequenceEqual(parameters)
                    )
                )
                .ToArray();
            if (methods.Length > 1)
                throw new AmbiguousMatchException(t.FullName + "." + name);
            if (methods.Length == 1)
                return generics == null ? methods[0] : methods[0].MakeGenericMethod(generics);
        }
        return null;
    }

    public static FieldInfo? Field(Type? type, string name)
    {
        for (var t = type; t != null; t = t.BaseType)
            if (t.GetField(name, Flags) is { } field)
                return field;
        return null;
    }

    public static PropertyInfo? Property(Type? type, string name)
    {
        for (var t = type; t != null; t = t.BaseType)
            if (t.GetProperty(name, Flags) is { } property)
                return property;
        return null;
    }

    public static List<MethodInfo> GetDeclaredMethods(Type type) => type.GetMethods(Flags).ToList();

    public static Type? TypeByName(string name) =>
        AppDomain
            .CurrentDomain.GetAssemblies()
            .Select(a => a.GetType(name, false))
            .FirstOrDefault(t => t != null);
}
