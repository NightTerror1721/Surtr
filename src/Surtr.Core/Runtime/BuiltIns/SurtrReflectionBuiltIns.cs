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
                isStatic: true);

            builder.Property("name", SurtrClassReference.String, SurtrNativeEntryPoint.FromFunctionPointer(&TypeName));
            builder.Property("baseType", selfType, SurtrNativeEntryPoint.FromFunctionPointer(&TypeBaseType));
            builder.Property("isInterface", SurtrClassReference.Boolean, SurtrNativeEntryPoint.FromFunctionPointer(&TypeIsInterface));
            builder.Method("members", memberArray, SurtrNativeEntryPoint.FromFunctionPointer(&TypeMembers));
            builder.Method("attributes", attributeArray, SurtrNativeEntryPoint.FromFunctionPointer(&TypeAttributes));
        }

        internal static void DeclareMember(SurtrBuiltInTypeBuilder builder)
        {
            var attributeArray = SurtrClassReference.Array(SurtrBuiltIns.Attribute.SelfReference);

            builder.Property("name", SurtrClassReference.String, SurtrNativeEntryPoint.FromFunctionPointer(&MemberName));
            builder.Property("kind", SurtrClassReference.String, SurtrNativeEntryPoint.FromFunctionPointer(&MemberKind));
            builder.Property("isStatic", SurtrClassReference.Boolean, SurtrNativeEntryPoint.FromFunctionPointer(&MemberIsStatic));
            builder.Property("declaringType", SurtrBuiltIns.Type.SelfReference, SurtrNativeEntryPoint.FromFunctionPointer(&MemberDeclaringType));
            builder.Method("attributes", attributeArray, SurtrNativeEntryPoint.FromFunctionPointer(&MemberAttributes));
        }

        #region Type
        private static SurtrValue TypeOf(SurtrCallArguments arguments)
        {
            // `unknown` is always a reference - the compiler boxes a primitive on the way in - so
            // there is no primitive case to handle here, unlike a receiver of a concrete class.
            var target = arguments.GetUnchecked<SurtrObject>(0);
            return WrapType(arguments.Runtime, target.GetClass());
        }

        private static SurtrValue TypeName(SurtrCallArguments arguments)
            => arguments.Runtime.NewStringValue(SelfType(arguments).Name);

        private static SurtrValue TypeBaseType(SurtrCallArguments arguments)
        {
            // An interface has no single base - only however many interfaces it extends - so it
            // answers null here exactly as the root of a class hierarchy already does.
            var baseType = (SelfType(arguments) as SurtrClass)?.BaseType?.ResolvedType;
            return baseType is null ? SurtrValue.Null : WrapType(arguments.Runtime, baseType);
        }

        private static SurtrValue TypeIsInterface(SurtrCallArguments arguments)
            => SurtrValue.CreateBool(SelfType(arguments).IsInterface);

        private static SurtrValue TypeMembers(SurtrCallArguments arguments)
        {
            var self = SelfType(arguments);
            var runtime = arguments.Runtime;
            var array = runtime.NewArray(SurtrClassReference.Array(SurtrBuiltIns.Member.SelfReference));

            // An interface is a pure contract: no fields, no nested types, no static members
            // (§"Runtime objects" / SurtrInterface's own remarks), so only its methods and
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

                return SurtrValue.CreateReference(array.GetSurtrReference());
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

            return SurtrValue.CreateReference(array.GetSurtrReference());
        }

        private static SurtrValue TypeAttributes(SurtrCallArguments arguments)
            => WrapAttributes(arguments.Runtime, SelfType(arguments).Attributes);

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
        /// method. §"Two naming conventions are ABI" names the leading <c>$</c> as exactly this
        /// signal, so a reflection API meant to show what a type's own author wrote hides them the
        /// same way source-level tooling already treats them as invisible.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static bool IsSynthetic(string name) => name.Length > 0 && name[0] == '$';

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static SurtrTypeInfo SelfType(SurtrCallArguments arguments) => arguments.GetUnchecked<SurtrTypeValue>(0).Wrapped;

        private static SurtrValue WrapType(SurtrRuntime runtime, SurtrTypeInfo wrapped)
            => SurtrValue.CreateReference(runtime.GetOrCreateTypeValue(wrapped).GetSurtrReference());
        #endregion

        #region Member
        private static SurtrValue MemberName(SurtrCallArguments arguments)
            => arguments.Runtime.NewStringValue(SelfMember(arguments).Name);

        private static SurtrValue MemberKind(SurtrCallArguments arguments)
            => arguments.Runtime.NewStringValue(KindName(SelfMember(arguments).Kind));

        private static SurtrValue MemberIsStatic(SurtrCallArguments arguments)
            => SurtrValue.CreateBool(SelfMember(arguments).IsStatic);

        private static SurtrValue MemberDeclaringType(SurtrCallArguments arguments)
        {
            var declaringType = SelfMember(arguments).DeclaringType?.ResolvedType;
            return declaringType is null ? SurtrValue.Null : WrapType(arguments.Runtime, declaringType);
        }

        private static SurtrValue MemberAttributes(SurtrCallArguments arguments)
            => WrapAttributes(arguments.Runtime, SelfMember(arguments).Attributes);

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
