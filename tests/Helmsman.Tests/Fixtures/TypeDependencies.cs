using System.Reflection;

namespace Helmsman.Tests.Fixtures;

/// <summary>
/// The types a type's compiled code names, read from its metadata and IL (not from source): for tests that pin which code may
/// depend on what (NamespaceDependencyTests). A constructed generic type counts as its definition and each of its arguments, an
/// array, pointer or by-ref type as its element type; generic parameters count only through their constraints.
/// </summary>
/// <remarks>
/// What the compiler leaves no trace of is not seen: a constant (const field, enum member used as a value) is copied into the
/// code that uses it, a nameof is a string, and a type used only in a doc comment or a using directive is not compiled. The
/// signature of a calli (an InlineSig token) is not read. The assembly's own attributes have no owner type and are not read.
/// </remarks>
public static class TypeDependencies
{
    /// <summary>
    /// The types <paramref name="type"/> and every type nested in it (its lambdas' closures, iterators and async state machines
    /// included) name: base types and implemented interfaces, field, property, event, parameter and return types, generic
    /// arguments and constraints, attributes with their constructor and named arguments (typeof and enum values), local
    /// variable and catch types, and the members and types their method bodies name (with each member's declaring type and
    /// signature). <paramref name="type"/> itself is in the set when its own code names it.
    /// </summary>
    public static IReadOnlySet<Type> Of(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var found = new HashSet<Type>();
        foreach (var current in SelfAndNested(type))
        {
            AddDeclaration(current, found);
        }

        return found;
    }

    private static IEnumerable<Type> SelfAndNested(Type type) =>
        new[] { type }.Concat(type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).SelectMany(SelfAndNested));

    private static void AddDeclaration(Type type, HashSet<Type> found)
    {
        AddType(type.BaseType, found);
        var inherited = type.BaseType?.GetInterfaces() ?? [];
        foreach (var implemented in type.GetInterfaces().Except(inherited))
        {
            AddType(implemented, found);
        }

        AddAttributes(type.GetCustomAttributesData(), found);
        if (type.IsGenericTypeDefinition)
        {
            AddGenericParameters(type.GetGenericArguments(), found);
        }

        foreach (var field in type.GetFields(IlCalls.DeclaredMembers))
        {
            AddType(field.FieldType, found);
            AddAttributes(field.GetCustomAttributesData(), found);
        }

        foreach (var property in type.GetProperties(IlCalls.DeclaredMembers))
        {
            AddType(property.PropertyType, found);
            AddAttributes(property.GetCustomAttributesData(), found);
            foreach (var index in property.GetIndexParameters())
            {
                AddType(index.ParameterType, found);
            }
        }

        foreach (var @event in type.GetEvents(IlCalls.DeclaredMembers))
        {
            AddType(@event.EventHandlerType, found);
            AddAttributes(@event.GetCustomAttributesData(), found);
        }

        foreach (var method in type.GetMethods(IlCalls.DeclaredMembers).Cast<MethodBase>().Concat(type.GetConstructors(IlCalls.DeclaredMembers)))
        {
            AddMethodDeclaration(method, found);
            AddMethodBody(method, found);
        }
    }

    private static void AddMethodDeclaration(MethodBase method, HashSet<Type> found)
    {
        AddAttributes(method.GetCustomAttributesData(), found);
        foreach (var parameter in method.GetParameters())
        {
            AddType(parameter.ParameterType, found);
            AddAttributes(parameter.GetCustomAttributesData(), found);
        }

        if (method is MethodInfo info)
        {
            AddType(info.ReturnType, found);
            AddAttributes(info.ReturnParameter.GetCustomAttributesData(), found);
            if (info.IsGenericMethodDefinition)
            {
                AddGenericParameters(info.GetGenericArguments(), found);
            }
        }
    }

    private static void AddMethodBody(MethodBase method, HashSet<Type> found)
    {
        var body = method.GetMethodBody();
        if (body == null)
        {
            return;
        }

        foreach (var local in body.LocalVariables)
        {
            AddType(local.LocalType, found);
        }

        foreach (var clause in body.ExceptionHandlingClauses.Where(c => c.Flags == ExceptionHandlingClauseOptions.Clause))
        {
            AddType(clause.CatchType, found);
        }

        foreach (var member in IlCalls.ReferencedMembers(method))
        {
            AddMember(member, found);
        }
    }

    /// <summary>A member a method body names: its declaring type, and its signature, which the caller compiles against.</summary>
    private static void AddMember(MemberInfo member, HashSet<Type> found)
    {
        switch (member)
        {
            case Type type:
                AddType(type, found);
                break;
            case FieldInfo field:
                AddType(field.DeclaringType, found);
                AddType(field.FieldType, found);
                break;
            case MethodBase method:
                AddType(method.DeclaringType, found);
                foreach (var parameter in method.GetParameters())
                {
                    AddType(parameter.ParameterType, found);
                }

                if (method is MethodInfo info)
                {
                    AddType(info.ReturnType, found);
                    if (info.IsGenericMethod)
                    {
                        foreach (var argument in info.GetGenericArguments())
                        {
                            AddType(argument, found);
                        }
                    }
                }

                break;
        }
    }

    private static void AddGenericParameters(Type[] parameters, HashSet<Type> found)
    {
        foreach (var parameter in parameters.Where(p => p.IsGenericParameter))
        {
            AddAttributes(parameter.GetCustomAttributesData(), found);
            foreach (var constraint in parameter.GetGenericParameterConstraints())
            {
                AddType(constraint, found);
            }
        }
    }

    private static void AddAttributes(IEnumerable<CustomAttributeData> attributes, HashSet<Type> found)
    {
        foreach (var attribute in attributes)
        {
            AddType(attribute.AttributeType, found);
            foreach (var argument in attribute.ConstructorArguments.Concat(attribute.NamedArguments.Select(a => a.TypedValue)))
            {
                AddAttributeArgument(argument, found);
            }
        }
    }

    /// <summary>An attribute argument: its type (an enum's, for an enum value) and the type a typeof argument names.</summary>
    private static void AddAttributeArgument(CustomAttributeTypedArgument argument, HashSet<Type> found)
    {
        AddType(argument.ArgumentType, found);
        switch (argument.Value)
        {
            case Type named:
                AddType(named, found);
                break;
            case IEnumerable<CustomAttributeTypedArgument> elements:
                foreach (var element in elements)
                {
                    AddAttributeArgument(element, found);
                }

                break;
        }
    }

    /// <summary>Adds the named types in <paramref name="type"/>: a generic definition and its arguments, an element type.</summary>
    private static void AddType(Type? type, HashSet<Type> found)
    {
        if (type == null || type.IsGenericParameter)
        {
            return;
        }

        if (type.HasElementType)
        {
            AddType(type.GetElementType(), found);
            return;
        }

        if (type.IsFunctionPointer)
        {
            AddType(type.GetFunctionPointerReturnType(), found);
            foreach (var parameter in type.GetFunctionPointerParameterTypes())
            {
                AddType(parameter, found);
            }

            return;
        }

        if (type.IsGenericType && !type.IsGenericTypeDefinition)
        {
            found.Add(type.GetGenericTypeDefinition());
            foreach (var argument in type.GetGenericArguments())
            {
                AddType(argument, found);
            }

            return;
        }

        found.Add(type);
    }
}
