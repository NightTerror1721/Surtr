#nullable enable

using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using System;
using System.Collections.Generic;

namespace Surtr.Runtime.BuiltIns
{
    /// <summary>
    /// Declares <c>Type</c> and <c>Member</c>: the reflection surface over a class's own
    /// metadata, read straight out of the same <see cref="SurtrClass"/>/<see cref="SurtrMemberInfo"/>
    /// tables the compiler and linker already keep - no second copy of anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately minimal: enumerate a type's own declared members, and read the attributes
    /// carried by a type or a member. Nothing here reads or invokes a member's value - that would
    /// need to reach through dispatch the way a compiled call site does, which is a different and
    /// much larger feature than "list what is here".
    /// </para>
    /// <para>
    /// Every attribute this API can ever hand back is already <c>Runtime</c>-retention: a
    /// <c>CompileTimeOnly</c> attribute never reaches <see cref="SurtrMemberInfo.Attributes"/> at
    /// all, because <c>ModuleEmitter</c> skips emitting it onto the member in the first place.
    /// There is nothing left to filter here.
    /// </para>
    /// </remarks>
    internal static unsafe class SurtrReflectionBuiltIns
    {
        internal static void DeclareType(SurtrBuiltInTypeBuilder builder)
        {
            var selfType = SurtrBuiltIns.Type.SelfReference;
            var memberArray = SurtrClassReference.Array(SurtrBuiltIns.Member.SelfReference);
            var attributeArray = SurtrClassReference.Array(SurtrBuiltIns.Attribute.SelfReference);

            builder.Method(
                "of",
                selfType,
                SurtrNativeEntryPoint.FromFunctionPointer(&TypeOf),
                builder.Params(("value", SurtrClassReference.Erased)),
                isStatic: true,
                isPure: true);

            builder.Method(
                "get",
                selfType,
                SurtrNativeEntryPoint.FromFunctionPointer(&TypeGet),
                builder.Params(("name", SurtrClassReference.String)),
                isStatic: true,
                isPure: true);

            builder.Method(
                "tryGet",
                selfType,
                SurtrNativeEntryPoint.FromFunctionPointer(&TypeTryGet),
                builder.Params(("name", SurtrClassReference.String)),
                isStatic: true,
                isPure: true);

            builder.Property("name", SurtrClassReference.String, SurtrNativeEntryPoint.FromFunctionPointer(&TypeName), isPure: true);
            builder.Property("baseType", selfType, SurtrNativeEntryPoint.FromFunctionPointer(&TypeBaseType), isPure: true);
            builder.Property("isInterface", SurtrClassReference.Boolean, SurtrNativeEntryPoint.FromFunctionPointer(&TypeIsInterface), isPure: true);
            builder.Property("descriptor", SurtrClassReference.String, SurtrNativeEntryPoint.FromFunctionPointer(&TypeDescriptor), isPure: true);
            builder.Property("genericParameterCount", SurtrClassReference.Integer, SurtrNativeEntryPoint.FromFunctionPointer(&TypeGenericParameterCount), isPure: true);
            builder.Method("genericParameters", SurtrClassReference.Array(SurtrClassReference.String), SurtrNativeEntryPoint.FromFunctionPointer(&TypeGenericParameters), isPure: true);
            builder.Method("genericConstraints", SurtrClassReference.Array(SurtrClassReference.Array(SurtrClassReference.String)), SurtrNativeEntryPoint.FromFunctionPointer(&TypeGenericConstraints), isPure: true);
            builder.Method("genericArguments", SurtrClassReference.Array(selfType), SurtrNativeEntryPoint.FromFunctionPointer(&TypeGenericArguments), isPure: true);
            builder.Method("members", memberArray, SurtrNativeEntryPoint.FromFunctionPointer(&TypeMembers), isPure: true);
            builder.Method("attributes", attributeArray, SurtrNativeEntryPoint.FromFunctionPointer(&TypeAttributes), isPure: true);
        }

        internal static void DeclareMember(SurtrBuiltInTypeBuilder builder)
        {
            var attributeArray = SurtrClassReference.Array(SurtrBuiltIns.Attribute.SelfReference);

            builder.Property("name", SurtrClassReference.String, SurtrNativeEntryPoint.FromFunctionPointer(&MemberName), isPure: true);
            builder.Property("kind", SurtrClassReference.String, SurtrNativeEntryPoint.FromFunctionPointer(&MemberKind), isPure: true);
            builder.Property("isStatic", SurtrClassReference.Boolean, SurtrNativeEntryPoint.FromFunctionPointer(&MemberIsStatic), isPure: true);
            builder.Property("declaringType", SurtrBuiltIns.Type.SelfReference, SurtrNativeEntryPoint.FromFunctionPointer(&MemberDeclaringType), isPure: true);
            builder.Method("attributes", attributeArray, SurtrNativeEntryPoint.FromFunctionPointer(&MemberAttributes), isPure: true);
        }

        #region Type
        private static int TypeOf(SurtrCallArguments arguments)
        {
            // `unknown` is always a reference - the compiler boxes a primitive on the way in - so
            // there is no primitive case to handle here, unlike a receiver of a concrete class.
            var target = arguments.GetUnchecked<SurtrObject>(0);
            return arguments.Return(WrapType(arguments.Runtime, target.GetClass()));
        }

        /// <summary>
        /// <c>name</c> is a descriptor (Â§"Type references are descriptor strings" -
        /// <c>Ogame.core:Entity;</c>, <c>AI</c> for <c>int[]</c>, a mangled generic construction),
        /// not a display name - the canonical, unambiguous form the runtime already resolves
        /// everything else against, reused here with zero new parsing rather than a second,
        /// friendlier grammar this API would have to maintain on its own.
        /// </summary>
        private static int TypeGet(SurtrCallArguments arguments)
        {
            var reference = SurtrClassReference.FromDescriptor(arguments.GetString(0).Text);
            if (!arguments.Runtime.TryResolveReference(reference, out var resolved))
                throw new KeyNotFoundException($"No type is known under descriptor '{reference.Descriptor}'.");

            return arguments.Return(WrapType(arguments.Runtime, resolved!, reference));
        }

        private static int TypeTryGet(SurtrCallArguments arguments)
        {
            var reference = SurtrClassReference.FromDescriptor(arguments.GetString(0).Text);
            return arguments.Return(arguments.Runtime.TryResolveReference(reference, out var resolved)
                ? WrapType(arguments.Runtime, resolved!, reference)
                : SurtrValue.Null);
        }

        private static int TypeName(SurtrCallArguments arguments)
            => arguments.Return(arguments.Runtime.NewStringValue(SelfType(arguments).Name));

        private static int TypeBaseType(SurtrCallArguments arguments)
        {
            // An interface has no single base - only however many interfaces it extends - so it
            // answers null here exactly as the root of a class hierarchy already does.
            var baseType = (SelfType(arguments) as SurtrClass)?.BaseType?.ResolvedType;
            return arguments.Return(baseType is null ? SurtrValue.Null : WrapType(arguments.Runtime, baseType));
        }

        private static int TypeIsInterface(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateBool(SelfType(arguments).IsInterface));

        /// <summary>
        /// The full descriptor this <c>Type</c> value came from â€” <c>Obox:Box`1;I</c> for
        /// <c>Type.get("Obox:Box`1;I")</c> or <c>typeof(Box&lt;int&gt;)</c> â€” or null when the
        /// value was reached from an instance (<c>Type.of</c>, <c>typeof(x)</c>), which cannot
        /// carry a construction. It is the canonical form, not the display name: <c>name</c> gives
        /// <c>Box</c> for every construction, <c>descriptor</c> tells them apart.
        /// </summary>
        private static int TypeDescriptor(SurtrCallArguments arguments)
        {
            var reference = SelfTypeValue(arguments).Reference;
            return arguments.Return(reference.IsValid
                ? arguments.Runtime.NewStringValue(reference.Descriptor)
                : SurtrValue.Null);
        }

        private static int TypeGenericParameterCount(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateInt(SelfType(arguments).GenericParameters.Length));

        private static int TypeGenericParameters(SurtrCallArguments arguments)
        {
            var runtime = arguments.Runtime;
            var names = SelfType(arguments).GenericParameters;
            var array = runtime.NewArray(SurtrClassReference.Array(SurtrClassReference.String), names.Length);
            for (int i = 0; i < names.Length; i++)
                array.Add(runtime.NewStringValue(names[i]));

            return arguments.Return(SurtrValue.CreateReference(array.GetSurtrReference()));
        }

        /// <summary>
        /// One <c>string[]</c> per generic parameter, each holding that parameter's bound
        /// descriptors â€” <c>Osurtr:IComparable`1;G0</c> for <c>T : IComparable&lt;T&gt;</c>. A
        /// parameter with no bounds yields an empty array. The bounds are descriptors, the same
        /// canonical form <c>Type.get</c> reads, so a caller can resolve them back to <c>Type</c>s.
        /// </summary>
        private static int TypeGenericConstraints(SurtrCallArguments arguments)
        {
            var runtime = arguments.Runtime;
            var constraints = SelfType(arguments).GenericConstraints;
            var innerType = SurtrClassReference.Array(SurtrClassReference.String);
            var outer = runtime.NewArray(SurtrClassReference.Array(innerType), constraints.Length);

            for (int i = 0; i < constraints.Length; i++)
            {
                var bounds = constraints[i];
                var inner = runtime.NewArray(innerType, bounds.Length);
                for (int b = 0; b < bounds.Length; b++)
                    inner.Add(runtime.NewStringValue(bounds[b]));

                outer.Add(SurtrValue.CreateReference(inner.GetSurtrReference()));
            }

            return arguments.Return(SurtrValue.CreateReference(outer.GetSurtrReference()));
        }

        /// <summary>
        /// The construction's arguments as <c>Type</c>s, in order â€” <c>[Type.of(int)]</c> for
        /// <c>Type.get("Obox:Box`1;I")</c>. Empty for a <c>Type</c> that did not come from a
        /// construction: the bare class, or one reached from an instance, which cannot say which
        /// construction it is. An argument that somehow fails to resolve (never for a closed form)
        /// yields null rather than a stale type.
        /// </summary>
        private static int TypeGenericArguments(SurtrCallArguments arguments)
        {
            var runtime = arguments.Runtime;
            var value = SelfTypeValue(arguments);
            var array = runtime.NewArray(SurtrClassReference.Array(SurtrBuiltIns.Type.SelfReference));

            foreach (var argument in value.Reference.GetTypeArguments())
            {
                array.Add(runtime.TryResolveReference(argument, out var resolved)
                    ? WrapType(runtime, resolved!, argument)
                    : SurtrValue.Null);
            }

            return arguments.Return(SurtrValue.CreateReference(array.GetSurtrReference()));
        }

        private static int TypeMembers(SurtrCallArguments arguments)
        {
            var self = SelfType(arguments);
            var runtime = arguments.Runtime;
            var array = runtime.NewArray(SurtrClassReference.Array(SurtrBuiltIns.Member.SelfReference));

            // An interface is a pure contract: no fields, no nested types, no static members
            // (Â§"Runtime objects" / SurtrInterface's own remarks), so only its methods and
            // properties are worth walking - there is nothing else to skip.
            if (self is SurtrInterface iface)
            {
                var ifaceAccessors = PropertyAccessors(iface.Properties);

                foreach (var property in iface.Properties)
                {
                    if (!IsSynthetic(property.Name))
                        array.Add(WrapMember(runtime, property));
                }

                foreach (var overloads in iface.Methods)
                {
                    for (int i = 0; i < overloads.Length; i++)
                    {
                        if (!ifaceAccessors.Contains(overloads[i]) && !IsSynthetic(overloads[i].Name))
                            array.Add(WrapMember(runtime, overloads[i]));
                    }
                }

                return arguments.Return(SurtrValue.CreateReference(array.GetSurtrReference()));
            }

            var cls = (SurtrClass)self;
            var accessors = PropertyAccessors(cls.Properties);

            foreach (var field in cls.Fields)
            {
                if (!IsSynthetic(field.Name))
                    array.Add(WrapMember(runtime, field));
            }

            foreach (var property in cls.Properties)
            {
                if (!IsSynthetic(property.Name))
                    array.Add(WrapMember(runtime, property));
            }

            foreach (var overloads in cls.Methods)
            {
                for (int i = 0; i < overloads.Length; i++)
                {
                    // A property's own get_x/set_x already appears once, as the property itself -
                    // showing it again under its accessor's own name would be the same
                    // declaration twice under two different faces.
                    if (accessors.Contains(overloads[i]))
                        continue;

                    if (!IsSynthetic(overloads[i].Name))
                        array.Add(WrapMember(runtime, overloads[i]));
                }
            }

            foreach (var nested in cls.NestedClasses)
                array.Add(WrapMember(runtime, nested));

            foreach (var nested in cls.NestedInterfaces)
                array.Add(WrapMember(runtime, nested));

            return arguments.Return(SurtrValue.CreateReference(array.GetSurtrReference()));
        }

        private static int TypeAttributes(SurtrCallArguments arguments)
            => arguments.Return(WrapAttributes(arguments.Runtime, SelfType(arguments).Attributes));

        private static HashSet<SurtrMethodInfo> PropertyAccessors(IEnumerable<SurtrPropertyInfo> properties)
        {
            var accessors = new HashSet<SurtrMethodInfo>();
            foreach (var property in properties)
            {
                if (property.Getter is not null)
                    accessors.Add(property.Getter);

                if (property.Setter is not null)
                    accessors.Add(property.Setter);
            }

            return accessors;
        }

        /// <summary>
        /// Whether <paramref name="name"/> is something the compiler made up rather than
        /// something the source declared - an auto-property's backing field, a lambda, a bridge
        /// method. Â§"Two naming conventions are ABI" names the leading <c>$</c> as exactly this
        /// signal, so a reflection API meant to show what a type's own author wrote hides them the
        /// same way source-level tooling already treats them as invisible.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static bool IsSynthetic(string name) => SurtrMetadataQuery.IsSynthetic(name);

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static SurtrTypeValue SelfTypeValue(SurtrCallArguments arguments) => arguments.GetUnchecked<SurtrTypeValue>(0);

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static SurtrTypeInfo SelfType(SurtrCallArguments arguments) => SelfTypeValue(arguments).Wrapped;

        private static SurtrValue WrapType(SurtrRuntime runtime, SurtrTypeInfo wrapped)
            => SurtrValue.CreateReference(runtime.GetOrCreateTypeValue(wrapped).GetSurtrReference());

        private static SurtrValue WrapType(SurtrRuntime runtime, SurtrTypeInfo wrapped, SurtrClassReference reference)
            => SurtrValue.CreateReference(runtime.GetOrCreateTypeValue(wrapped, reference).GetSurtrReference());
        #endregion

        #region Member
        private static int MemberName(SurtrCallArguments arguments)
            => arguments.Return(arguments.Runtime.NewStringValue(SelfMember(arguments).Name));

        private static int MemberKind(SurtrCallArguments arguments)
            => arguments.Return(arguments.Runtime.NewStringValue(KindName(SelfMember(arguments).Kind)));

        private static int MemberIsStatic(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateBool(SelfMember(arguments).IsStatic));

        private static int MemberDeclaringType(SurtrCallArguments arguments)
        {
            var declaringType = SelfMember(arguments).DeclaringType?.ResolvedType;
            return arguments.Return(declaringType is null ? SurtrValue.Null : WrapType(arguments.Runtime, declaringType));
        }

        private static int MemberAttributes(SurtrCallArguments arguments)
            => arguments.Return(WrapAttributes(arguments.Runtime, SelfMember(arguments).Attributes));

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static SurtrMemberInfo SelfMember(SurtrCallArguments arguments) => arguments.GetUnchecked<SurtrMemberValue>(0).Wrapped;

        private static string KindName(SurtrMemberKind kind) => kind switch
        {
            SurtrMemberKind.Field => "field",
            SurtrMemberKind.Property => "property",
            SurtrMemberKind.Method => "method",
            SurtrMemberKind.Class => "class",
            SurtrMemberKind.Enum => "enum",
            SurtrMemberKind.Interface => "interface",
            _ => "invalid",
        };
        #endregion

        private static SurtrValue WrapMember(SurtrRuntime runtime, SurtrMemberInfo member)
            => SurtrValue.CreateReference(runtime.NewMemberValue(member).GetSurtrReference());

        private static SurtrValue WrapAttributes(SurtrRuntime runtime, ReadOnlySpan<SurtrAttributeUsage> attributes)
        {
            var array = runtime.NewArray(SurtrClassReference.Array(SurtrBuiltIns.Attribute.SelfReference), attributes.Length);
            for (int i = 0; i < attributes.Length; i++)
                array.Add(SurtrValue.CreateReference(attributes[i].Instance));

            return SurtrValue.CreateReference(array.GetSurtrReference());
        }
    }
}
