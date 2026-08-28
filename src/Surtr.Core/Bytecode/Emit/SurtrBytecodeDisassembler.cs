#nullable enable

using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Surtr.Bytecode.Emit
{
    /// <summary>
    /// Controls how much a <see cref="SurtrBytecodeDisassembler"/> rendering shows beyond the bare
    /// instruction stream.
    /// </summary>
    /// <remarks>
    /// Only <see cref="Detailed"/> and <see cref="ShowRawBytes"/> gate anything: structural
    /// information that changes what a member <em>is</em> - native and abstract members, class and
    /// interface grouping, fields, properties, enum cases - always renders, because hiding it is
    /// what made the old flat listing misleading. What <see cref="Detailed"/> turns off is the
    /// bulkiest purely-additive material: attribute usages, the tabular access-table dump and the
    /// pending-reference listing for an unloaded image.
    /// </remarks>
    public readonly struct SurtrDisassemblyOptions
    {
        /// <summary>Everything this file can show.</summary>
        public static readonly SurtrDisassemblyOptions Default = new SurtrDisassemblyOptions(detailed: true, showRawBytes: false);

        /// <summary>Structure and bodies, without attributes, access-table dumps or pending references.</summary>
        public static readonly SurtrDisassemblyOptions Compact = new SurtrDisassemblyOptions(detailed: false, showRawBytes: false);

        /// <summary>Whether attribute usages, tabular pool dumps and pending references are shown.</summary>
        public bool Detailed { get; }

        /// <summary>Whether each instruction is followed by its raw encoded bytes.</summary>
        public bool ShowRawBytes { get; }

        /// <summary>Creates an explicit combination of both switches.</summary>
        public SurtrDisassemblyOptions(bool detailed, bool showRawBytes)
        {
            Detailed = detailed;
            ShowRawBytes = showRawBytes;
        }

        /// <summary>Returns a copy with <see cref="ShowRawBytes"/> set.</summary>
        public SurtrDisassemblyOptions WithRawBytes(bool value = true) => new SurtrDisassemblyOptions(Detailed, value);
    }

    /// <summary>
    /// Renders a built module's bytecode as text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For debugging an emitter and for asserting on emitted code in tests. It decodes the same
    /// byte layout the interpreter does, so a disagreement between the two shows up as garbled
    /// output rather than as a wrong answer at run time - which makes this the cheapest check that
    /// a newly written emit path lays its immediates out the way the opcode says it should.
    /// </para>
    /// <para>
    /// A rendering follows source structure rather than chunk layout: module and class members are
    /// grouped the way they were declared - fields, enum cases, properties, then methods, then
    /// nested types - each group sorted by name for a reproducible, diffable listing, rather than by
    /// the load-bearing but otherwise meaningless order a name-keyed dictionary happens to enumerate
    /// in. A native or abstract member has no instruction stream, but it is a member all the same and
    /// is rendered with everything <em>except</em> a body - the old flat, bytecode-only listing left
    /// those invisible.
    /// </para>
    /// <para>
    /// This reads a module the way an emitter left it, before <c>SurtrRuntime.LoadModule</c> has
    /// linked it: declared bases, interfaces and generic parameters are all available at that point,
    /// but flattened, load-time-only tables (ancestor chains, vtable slot assignments, static
    /// storage) are not, so slot numbers are only shown once assigned (never negative) and the
    /// flattened interface/ancestor tables are not attempted.
    /// </para>
    /// <para>
    /// This is diagnostic code and makes no attempt to be fast or allocation-free.
    /// </para>
    /// </remarks>
    public static class SurtrBytecodeDisassembler
    {
        /// <summary>Renders a whole module: its pools, its declarations and every body, in <see cref="SurtrDisassemblyOptions.Default"/>.</summary>
        /// <exception cref="InvalidOperationException">The module's chunk has not been built.</exception>
        public static string Disassemble(SurtrModule module) => Disassemble(module, SurtrDisassemblyOptions.Default);

        /// <summary>Renders a whole module under the given <paramref name="options"/>.</summary>
        /// <exception cref="InvalidOperationException">The module's chunk has not been built.</exception>
        public static string Disassemble(SurtrModule module, SurtrDisassemblyOptions options)
        {
            if (module is null)
                throw new ArgumentNullException(nameof(module));

            var chunk = module.Chunk;
            var builder = new StringBuilder();

            builder.Append("module ").Append(module.Path).AppendLine();
            AppendPools(builder, chunk);

            if (options.Detailed)
            {
                AppendAccessTables(builder, chunk);
                AppendPendingReferences(builder, chunk);
            }

            if (module.Fields.Count != 0)
            {
                builder.AppendLine();
                foreach (var field in OrderedByName(module.Fields))
                    AppendField(builder, options, "  ", field);
            }

            var accessorMethods = CollectAccessorMethods(module.Properties);

            foreach (var group in OrderedMethodGroups(module.Methods))
            {
                for (int i = 0; i < group.Length; i++)
                {
                    if (accessorMethods.Contains(group[i]))
                        continue;

                    builder.AppendLine();
                    AppendMethod(builder, chunk, "", group[i], options);
                }
            }

            foreach (var property in OrderedByName(module.Properties))
            {
                builder.AppendLine();
                AppendProperty(builder, chunk, "", property, options);
            }

            foreach (var iface in OrderedByName(module.Interfaces))
            {
                builder.AppendLine();
                AppendInterface(builder, chunk, iface, options, "");
            }

            foreach (var declared in OrderedByName(module.Classes))
            {
                builder.AppendLine();
                AppendClass(builder, chunk, declared, options, "");
            }

            return builder.ToString();
        }

        /// <summary>Renders a single method's header and body, in <see cref="SurtrDisassemblyOptions.Default"/>.</summary>
        public static string Disassemble(SurtrBytecodeMethodInfo method) => Disassemble(method, SurtrDisassemblyOptions.Default);

        /// <summary>Renders a single method's header and body under the given <paramref name="options"/>.</summary>
        public static string Disassemble(SurtrBytecodeMethodInfo method, SurtrDisassemblyOptions options)
        {
            if (method is null)
                throw new ArgumentNullException(nameof(method));

            var builder = new StringBuilder();

            if (options.Detailed)
                AppendAttributes(builder, "", method);

            AppendMethodHeader(builder, "", method);
            AppendBytecodeBody(builder, method.Chunk, "  ", method, options);
            return builder.ToString();
        }

        #region Pools and access tables

        private static void AppendPools(StringBuilder builder, SurtrChunk chunk)
        {
            builder.Append("  constants: ").Append(chunk.Constants.Length)
                   .Append("   types: ").Append(chunk.TypeTable.Length)
                   .Append("   fields: ").Append(chunk.FieldTable.Length)
                   .Append("   methods: ").Append(chunk.MethodTable.Length)
                   .Append("   modules: ").Append(chunk.ModuleTable.Length)
                   .AppendLine();

            for (int i = 0; i < chunk.StringConstants.Length; i++)
            {
                builder.Append("  string #").Append(chunk.StringConstantSlots[i])
                       .Append(" = \"").Append(chunk.StringConstants[i]).Append('"')
                       .AppendLine();
            }
        }

        /// <summary>
        /// Dumps every access table by index, so a reader does not have to scan the whole
        /// instruction stream to learn what table slot 7 holds.
        /// </summary>
        private static void AppendAccessTables(StringBuilder builder, SurtrChunk chunk)
        {
            if (chunk.TypeTable.Length != 0)
            {
                builder.AppendLine();
                builder.Append("  type table:").AppendLine();
                for (int i = 0; i < chunk.TypeTable.Length; i++)
                {
                    builder.Append("    #").Append(i).Append("  ")
                           .Append(chunk.TypeTable[i].Reference.ToDisplayString())
                           .AppendLine();
                }
            }

            if (chunk.FieldTable.Length != 0)
            {
                builder.AppendLine();
                builder.Append("  field table:").AppendLine();
                for (int i = 0; i < chunk.FieldTable.Length; i++)
                {
                    var field = chunk.FieldTable[i];
                    builder.Append("    #").Append(i).Append("  ");

                    if (field is null)
                    {
                        builder.Append("<pending>").AppendLine();
                        continue;
                    }

                    AppendOwnerPrefix(builder, field.DeclaringType);
                    builder.Append(field.Name).Append(": ").Append(field.FieldType.Reference.ToDisplayString()).AppendLine();
                }
            }

            if (chunk.MethodTable.Length != 0)
            {
                builder.AppendLine();
                builder.Append("  method table:").AppendLine();
                for (int i = 0; i < chunk.MethodTable.Length; i++)
                {
                    var method = chunk.MethodTable[i];
                    builder.Append("    #").Append(i).Append("  ");

                    if (method is null)
                    {
                        builder.Append("<pending>").AppendLine();
                        continue;
                    }

                    AppendOwnerPrefix(builder, method.DeclaringType);
                    builder.Append(method.Name).Append(method.ToSignature().ToDisplayString()).AppendLine();
                }
            }

            if (chunk.ModuleTable.Length != 0)
            {
                builder.AppendLine();
                builder.Append("  module table:").AppendLine();
                for (int i = 0; i < chunk.ModuleTable.Length; i++)
                {
                    var other = chunk.ModuleTable[i];
                    builder.Append("    #").Append(i).Append("  ").Append(other is null ? "<pending>" : other.Path).AppendLine();
                }
            }
        }

        private static void AppendOwnerPrefix(StringBuilder builder, SurtrTypeHandle? declaringType)
        {
            if (declaringType is { } handle)
                builder.Append(handle.Reference.ToDisplayString()).Append('.');
        }

        /// <summary>
        /// Lists what a chunk read from an image still names by text rather than by object -
        /// useful for tracking down why a load failed to bind something.
        /// </summary>
        private static void AppendPendingReferences(StringBuilder builder, SurtrChunk chunk)
        {
            if (!chunk.HasPendingReferences)
                return;

            builder.AppendLine();
            builder.Append("  pending references (unresolved image):").AppendLine();

            for (int i = 0; i < chunk.PendingModulePaths.Length; i++)
            {
                builder.Append("    module #").Append(i).Append(" -> \"")
                       .Append(chunk.PendingModulePaths[i]).Append('"').AppendLine();
            }

            for (int i = 0; i < chunk.PendingFields.Length; i++)
            {
                var pending = chunk.PendingFields[i];
                builder.Append("    field #").Append(i).Append(" -> ")
                       .Append(pending.OwnerDescriptor ?? "<module>").Append('.').Append(pending.Name)
                       .AppendLine();
            }

            for (int i = 0; i < chunk.PendingMethods.Length; i++)
            {
                var pending = chunk.PendingMethods[i];
                builder.Append("    method #").Append(i).Append(" -> ")
                       .Append(pending.OwnerDescriptor ?? "<module>").Append('.').Append(pending.Name)
                       .Append(pending.SignatureKey ?? "()")
                       .AppendLine();
            }
        }

        #endregion

        #region Declarations: fields, enum cases, attributes, generics

        private static void AppendField(StringBuilder builder, SurtrDisassemblyOptions options, string indent, SurtrFieldInfo field)
        {
            if (options.Detailed)
                AppendAttributes(builder, indent, field);

            builder.Append(indent).Append('[').Append(VisibilityText(field.Visibility)).Append(']').Append(' ');

            if (field.IsStatic)
                builder.Append("static ");

            if (field.IsReadOnly)
                builder.Append("readonly ");

            builder.Append("field ").Append(field.Name).Append(": ").Append(field.FieldType.Reference.ToDisplayString());

            if (field.Slot >= 0)
                builder.Append("  ; slot ").Append(field.Slot);

            builder.AppendLine();
        }

        private static void AppendEnumCase(StringBuilder builder, string indent, SurtrEnumCaseInfo enumCase)
        {
            builder.Append(indent).Append("case ").Append(enumCase.Name).Append(" = ").Append(enumCase.Value);

            if (enumCase.Field.Slot >= 0)
                builder.Append("  ; slot ").Append(enumCase.Field.Slot);

            builder.AppendLine();
        }

        private static void AppendAttributes(StringBuilder builder, string indent, SurtrMemberInfo member)
        {
            var attributes = member.Attributes;
            for (int i = 0; i < attributes.Length; i++)
            {
                builder.Append(indent).Append('@').Append(attributes[i].AttributeType.Reference.ToDisplayString());

                var arguments = attributes[i].Arguments;
                if (arguments.Length != 0)
                {
                    builder.Append('(');
                    for (int a = 0; a < arguments.Length; a++)
                    {
                        if (a != 0)
                            builder.Append(", ");

                        builder.Append(arguments[a].ToString());
                    }
                    builder.Append(')');
                }

                builder.AppendLine();
            }
        }

        private static void AppendGenericParameters(StringBuilder builder, SurtrTypeInfo type)
        {
            var generics = type.GenericParameters;
            if (generics.Length == 0)
                return;

            builder.Append('<');
            for (int i = 0; i < generics.Length; i++)
            {
                if (i != 0)
                    builder.Append(", ");

                builder.Append(generics[i]);
            }
            builder.Append('>');
        }

        private static string VisibilityText(SurtrVisibility visibility) => visibility switch
        {
            SurtrVisibility.Private => "private",
            SurtrVisibility.Internal => "internal",
            SurtrVisibility.Protected => "protected",
            SurtrVisibility.Public => "public",
            _ => visibility.ToString(),
        };

        /// <summary>Every item, sorted by name, for a reproducible listing independent of dictionary enumeration order.</summary>
        private static List<T> OrderedByName<T>(IEnumerable<T> items) where T : SurtrMemberInfo
        {
            var list = new List<T>(items);
            list.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
            return list;
        }

        /// <summary>Overload groups, sorted by the name they share, with each group's own declaration order left untouched.</summary>
        private static List<SurtrMethodInfo[]> OrderedMethodGroups(IEnumerable<SurtrMethodInfo[]> groups)
        {
            var list = new List<SurtrMethodInfo[]>(groups);
            list.Sort(static (a, b) => string.CompareOrdinal(a[0].Name, b[0].Name));
            return list;
        }

        /// <summary>
        /// Every getter/setter reachable from a set of properties, so the flat method listing can
        /// skip them - they are rendered once, under their property, with a body instead of a bare
        /// signature.
        /// </summary>
        private static HashSet<SurtrMethodInfo> CollectAccessorMethods(IEnumerable<SurtrPropertyInfo> properties)
        {
            var accessors = new HashSet<SurtrMethodInfo>();
            foreach (var property in properties)
            {
                if (property.Getter is { } getter)
                    accessors.Add(getter);

                if (property.Setter is { } setter)
                    accessors.Add(setter);
            }
            return accessors;
        }

        #endregion

        #region Types: classes and interfaces

        private static void AppendClass(StringBuilder builder, SurtrChunk chunk, SurtrClass declared, SurtrDisassemblyOptions options, string indent)
        {
            if (options.Detailed)
                AppendAttributes(builder, indent, declared);

            builder.Append(indent);
            builder.Append(declared.IsEnum ? "enum " : declared.IsAbstract ? "abstract class " : declared.IsSealed ? "sealed class " : "class ");
            builder.Append(declared.Name);
            AppendGenericParameters(builder, declared);

            bool wroteBase = false;
            if (declared.BaseType is { } baseType)
            {
                builder.Append(" : ").Append(baseType.Reference.ToDisplayString());
                wroteBase = true;
            }

            var declaredInterfaces = declared.DeclaredInterfaceHandles;
            for (int i = 0; i < declaredInterfaces.Length; i++)
            {
                builder.Append(!wroteBase && i == 0 ? " : " : ", ");
                builder.Append(declaredInterfaces[i].Reference.ToDisplayString());
                wroteBase = true;
            }

            builder.Append("  [").Append(VisibilityText(declared.Visibility)).Append(']').AppendLine();

            string inner = indent + "  ";

            foreach (var field in OrderedByName(declared.Fields))
                AppendField(builder, options, inner, field);

            if (declared.IsEnum)
            {
                var cases = declared.EnumCases;
                for (int i = 0; i < cases.Length; i++)
                    AppendEnumCase(builder, inner, cases[i]);
            }

            var accessorMethods = CollectAccessorMethods(declared.Properties);

            foreach (var property in OrderedByName(declared.Properties))
            {
                builder.AppendLine();
                AppendProperty(builder, chunk, inner, property, options);
            }

            foreach (var group in OrderedMethodGroups(declared.Methods))
            {
                for (int i = 0; i < group.Length; i++)
                {
                    if (accessorMethods.Contains(group[i]))
                        continue;

                    builder.AppendLine();
                    AppendMethod(builder, chunk, inner, group[i], options);
                }
            }

            foreach (var nestedInterface in OrderedByName(declared.NestedInterfaces))
            {
                builder.AppendLine();
                AppendInterface(builder, chunk, nestedInterface, options, inner);
            }

            foreach (var nestedClass in OrderedByName(declared.NestedClasses))
            {
                builder.AppendLine();
                AppendClass(builder, chunk, nestedClass, options, inner);
            }
        }

        private static void AppendInterface(StringBuilder builder, SurtrChunk chunk, SurtrInterface declared, SurtrDisassemblyOptions options, string indent)
        {
            if (options.Detailed)
                AppendAttributes(builder, indent, declared);

            builder.Append(indent).Append("interface ").Append(declared.Name);
            AppendGenericParameters(builder, declared);

            var extended = declared.DeclaredExtendedInterfaceHandles;
            for (int i = 0; i < extended.Length; i++)
            {
                builder.Append(i == 0 ? " : " : ", ");
                builder.Append(extended[i].Reference.ToDisplayString());
            }

            builder.Append("  [").Append(VisibilityText(declared.Visibility)).Append(']').AppendLine();

            string inner = indent + "  ";
            var accessorMethods = CollectAccessorMethods(declared.Properties);

            foreach (var property in OrderedByName(declared.Properties))
            {
                builder.AppendLine();
                AppendProperty(builder, chunk, inner, property, options);
            }

            foreach (var group in OrderedMethodGroups(declared.Methods))
            {
                for (int i = 0; i < group.Length; i++)
                {
                    if (accessorMethods.Contains(group[i]))
                        continue;

                    builder.AppendLine();
                    AppendMethod(builder, chunk, inner, group[i], options);
                }
            }
        }

        #endregion

        #region Properties and methods

        private static void AppendProperty(StringBuilder builder, SurtrChunk chunk, string indent, SurtrPropertyInfo property, SurtrDisassemblyOptions options)
        {
            if (options.Detailed)
                AppendAttributes(builder, indent, property);

            builder.Append(indent).Append('[').Append(VisibilityText(property.Visibility)).Append(']').Append(' ');

            if (property.IsStatic)
                builder.Append("static ");

            builder.Append("property ").Append(property.Name).Append(": ").Append(property.PropertyType.Reference.ToDisplayString())
                   .Append(" { ")
                   .Append(property.Getter is null ? string.Empty : "get; ")
                   .Append(property.Setter is null ? string.Empty : "set; ")
                   .Append('}')
                   .AppendLine();

            string inner = indent + "  ";

            if (property.Getter is { } getter)
            {
                builder.Append(inner).Append("get:").AppendLine();
                AppendAccessorBody(builder, chunk, inner + "  ", getter, options);
            }

            if (property.Setter is { } setter)
            {
                builder.Append(inner).Append("set:").AppendLine();
                AppendAccessorBody(builder, chunk, inner + "  ", setter, options);
            }
        }

        private static void AppendAccessorBody(StringBuilder builder, SurtrChunk chunk, string indent, SurtrMethodInfo accessor, SurtrDisassemblyOptions options)
        {
            switch (accessor.ImplKind)
            {
                case SurtrMethodImplKind.Bytecode:
                    AppendBytecodeBody(builder, chunk, indent, (SurtrBytecodeMethodInfo)accessor, options);
                    break;

                case SurtrMethodImplKind.Native:
                    AppendNativeMeta(builder, indent, (SurtrNativeMethodInfo)accessor);
                    break;

                case SurtrMethodImplKind.Abstract:
                    builder.Append(indent).Append("; abstract").AppendLine();
                    break;
            }
        }

        private static void AppendMethod(StringBuilder builder, SurtrChunk chunk, string indent, SurtrMethodInfo method, SurtrDisassemblyOptions options)
        {
            if (options.Detailed)
                AppendAttributes(builder, indent, method);

            AppendMethodHeader(builder, indent, method);

            // One level deeper than the header, matching how a property nests its accessor
            // bodies under "get:"/"set:" - a method is its own implicit "body:" label.
            string bodyIndent = indent + "  ";

            switch (method.ImplKind)
            {
                case SurtrMethodImplKind.Bytecode:
                    AppendBytecodeBody(builder, chunk, bodyIndent, (SurtrBytecodeMethodInfo)method, options);
                    break;

                case SurtrMethodImplKind.Native:
                    AppendNativeMeta(builder, bodyIndent, (SurtrNativeMethodInfo)method);
                    break;

                // Abstract: the header already says so; there is no body to show.
            }
        }

        /// <summary>
        /// Renders a method's full declaration line: visibility, every modifier
        /// (<see cref="SurtrMethodImplKind"/>, <see cref="SurtrMethodDispatch"/>,
        /// <see cref="SurtrMethodRole"/>, override/sealed), its name, its parameters with names,
        /// defaults and varargs marks, and its return type.
        /// </summary>
        private static void AppendMethodHeader(StringBuilder builder, string indent, SurtrMethodInfo method)
        {
            builder.Append(indent).Append('[').Append(VisibilityText(method.Visibility)).Append(']').Append(' ');

            if (method.IsStatic)
                builder.Append("static ");

            if (method.ImplKind == SurtrMethodImplKind.Native)
                builder.Append("native ");

            switch (method.Dispatch)
            {
                case SurtrMethodDispatch.Virtual:
                    builder.Append("virtual ");
                    break;
                case SurtrMethodDispatch.Abstract:
                    builder.Append("abstract ");
                    break;
            }

            if (method.IsOverride)
                builder.Append("override ");

            if (method.IsSealed)
                builder.Append("sealed ");

            switch (method.Role)
            {
                case SurtrMethodRole.Constructor:
                    builder.Append("ctor ");
                    break;
                case SurtrMethodRole.StaticInitializer:
                    builder.Append("static-init ");
                    break;
            }

            // A constructor's own name is always "ctor" by convention (DefineConstructor's
            // default), so printing it after the "ctor " role tag would just repeat the word;
            // every other role's name carries real information and stays.
            if (method.Role != SurtrMethodRole.Constructor)
                builder.Append(method.Name);

            builder.Append('(');

            var parameters = method.Parameters;
            for (int i = 0; i < parameters.Length; i++)
            {
                if (i != 0)
                    builder.Append(", ");

                var parameter = parameters[i];
                builder.Append(parameter.Name).Append(": ").Append(parameter.ParameterType.Reference.ToDisplayString());

                if (parameter.IsVarargs)
                    builder.Append("...");
                else if (parameter.HasDefault)
                    builder.Append(" = ").Append(parameter.DefaultValue.ToString());
            }

            builder.Append("): ").Append(method.ReturnType.Reference.ToDisplayString());

            if (method.Dispatch != SurtrMethodDispatch.Direct && method.VTableSlot >= 0)
                builder.Append("  ; vtable ").Append(method.VTableSlot);

            builder.AppendLine();
        }

        private static void AppendNativeMeta(StringBuilder builder, string indent, SurtrNativeMethodInfo method)
        {
            builder.Append(indent).Append("; native link=\"").Append(method.LinkName).Append('"')
                   .Append(method.IsBound ? " bound" : " unbound")
                   .Append(method.HasDeclaredLinkName ? " declared" : " derived")
                   .AppendLine();
        }

        #endregion

        #region Bytecode bodies

        /// <summary>
        /// Renders a bytecode method's frame summary, its whole instruction stream - with symbolic
        /// jump labels and inline <c>try</c>/<c>catch</c> markers - and its handler table.
        /// </summary>
        private static void AppendBytecodeBody(StringBuilder builder, SurtrChunk chunk, string indent, SurtrBytecodeMethodInfo method, SurtrDisassemblyOptions options)
        {
            int start = method.CodeOffset;
            int end = EndOffsetOf(chunk, start);

            builder.Append(indent).Append("; entry ").Append(method.EntryIndex)
                   .Append(", locals ").Append(method.LocalCount)
                   .Append(", stack ").Append(method.MaxStackSize)
                   .Append(", code ").Append(Hex(start)).Append("..").Append(Hex(end))
                   .AppendLine();

            var handlers = method.Handlers;

            // First pass: decode into a scratch buffer purely to learn every branch target inside
            // this body, reusing the exact same decode switch a second time rather than keeping a
            // parallel length table in sync with it by hand.
            var jumpTargets = new List<int>();
            var scratch = new StringBuilder();
            int scan = start;
            while (scan < end)
                scan = AppendInstruction(scratch, chunk, scan, jumpTargets, labels: null, indent: string.Empty);

            var labels = BuildLabelMap(jumpTargets, start, end);
            var markers = BuildHandlerMarkers(handlers);

            int position = start;
            while (position < end)
            {
                if (markers.TryGetValue(position, out var atPosition))
                {
                    for (int i = 0; i < atPosition.Count; i++)
                        builder.Append(indent).Append("; ").Append(atPosition[i]).AppendLine();
                }

                if (labels.TryGetValue(position, out string? label))
                    builder.Append(indent).Append(label).Append(':').AppendLine();

                int next = AppendInstruction(builder, chunk, position, jumpTargets: null, labels, indent);

                if (options.ShowRawBytes)
                    AppendRawBytes(builder, chunk, indent, position, next);

                position = next;
            }

            for (int i = 0; i < handlers.Length; i++)
            {
                builder.Append(indent).Append("handler [").Append(Hex(handlers[i].TryStart))
                       .Append(", ").Append(Hex(handlers[i].TryEnd)).Append(") -> ")
                       .Append(Hex(handlers[i].HandlerOffset))
                       .Append("  catch ")
                       .Append(handlers[i].CatchType is null ? "*" : handlers[i].CatchType!.Reference.ToDisplayString())
                       .AppendLine();
            }
        }

        /// <summary>Assigns "L0", "L1", ... to every distinct in-range branch target, in address order.</summary>
        private static Dictionary<int, string> BuildLabelMap(List<int> jumpTargets, int start, int end)
        {
            var distinct = new SortedSet<int>();
            for (int i = 0; i < jumpTargets.Count; i++)
            {
                int target = jumpTargets[i];

                // A target outside the method is either a malformed instruction (a test feeding
                // the decoder garbage immediates) or the space right past the last real
                // instruction; either way it is not a position this method can attach a label to.
                if (target >= start && target < end)
                    distinct.Add(target);
            }

            var labels = new Dictionary<int, string>(distinct.Count);
            int n = 0;
            foreach (int target in distinct)
                labels[target] = "L" + n++;

            return labels;
        }

        /// <summary>Groups the human-readable <c>try</c>/<c>catch</c> markers by the offset they annotate.</summary>
        private static Dictionary<int, List<string>> BuildHandlerMarkers(SurtrExceptionHandler[] handlers)
        {
            var markers = new Dictionary<int, List<string>>();

            for (int i = 0; i < handlers.Length; i++)
            {
                AddMarker(markers, handlers[i].TryStart, "try_" + i + " begin");
                AddMarker(markers, handlers[i].TryEnd, "try_" + i + " end");

                string catchText = handlers[i].CatchType is null ? "*" : handlers[i].CatchType!.Reference.ToDisplayString();
                AddMarker(markers, handlers[i].HandlerOffset, "catch_" + i + " (" + catchText + ")");
            }

            return markers;
        }

        private static void AddMarker(Dictionary<int, List<string>> markers, int offset, string text)
        {
            if (!markers.TryGetValue(offset, out var atOffset))
                markers[offset] = atOffset = new List<string>();

            atOffset.Add(text);
        }

        private static void AppendRawBytes(StringBuilder builder, SurtrChunk chunk, string indent, int start, int end)
        {
            builder.Append(indent).Append("; bytes:");
            for (int i = start; i < end; i++)
                builder.Append(' ').Append(chunk.Code[i].ToString("X2", CultureInfo.InvariantCulture));

            builder.AppendLine();
        }

        /// <summary>
        /// Where a body ends: at the start of whichever body begins after it, or at the end of the
        /// chunk when nothing does.
        /// </summary>
        /// <remarks>
        /// Methods carry a start offset and nothing else, because a body always runs to the next
        /// one - the emitter lays them out back to back in one stream.
        /// </remarks>
        private static int EndOffsetOf(SurtrChunk chunk, int start)
        {
            int end = chunk.Code.Length;

            for (int i = 0; i < chunk.MethodOffsets.Length; i++)
            {
                int candidate = chunk.MethodOffsets[i];
                if (candidate > start && candidate < end)
                    end = candidate;
            }

            return end;
        }

        #endregion

        #region Instruction decoding

        private static int AppendInstruction(
            StringBuilder builder,
            SurtrChunk chunk,
            int position,
            List<int>? jumpTargets,
            Dictionary<int, string>? labels,
            string indent)
        {
            var op = (OpCode)chunk.Code[position];
            int operand = position + 1;

            // The prefix is not an instruction, so it is not printed as one: the line names the
            // sub-opcode, and the whole two-byte header is accounted for before any immediate is
            // read. Decoding lives in its own method for the same reason the interpreter's lives
            // in its own switch - the two spaces are independent.
            if (op == OpCode.Ext)
                return AppendExtInstruction(builder, chunk, position, jumpTargets, labels, indent);

            if (op == OpCode.Wide)
                return AppendWideInstruction(builder, chunk, position, jumpTargets, labels, indent);

            builder.Append(indent).Append("  ").Append(Hex(position)).Append("  ").Append(op.ToString());

            switch (op)
            {
                // ---- no immediate ------------------------------------------------------------
                case OpCode.Nop:
                case OpCode.Dup:
                case OpCode.PushNull:
                case OpCode.PushTrue:
                case OpCode.PushFalse:
                case OpCode.Pop:
                case OpCode.Ldc0:
                case OpCode.Ldc1:
                case OpCode.Ldc2:
                case OpCode.Ldc3:
                case OpCode.Ldc4:
                case OpCode.Ldc5:
                case OpCode.Ldc6:
                case OpCode.Ldc7:
                case OpCode.Ldc8:
                case OpCode.Ldc9:
                case OpCode.Ldl0:
                case OpCode.Ldl1:
                case OpCode.Ldl2:
                case OpCode.Ldl3:
                case OpCode.Ldl4:
                case OpCode.Ldl5:
                case OpCode.Stl0:
                case OpCode.Stl1:
                case OpCode.Stl2:
                case OpCode.Stl3:
                case OpCode.Stl4:
                case OpCode.Stl5:
                case OpCode.Add:
                case OpCode.FAdd:
                case OpCode.Sub:
                case OpCode.FSub:
                case OpCode.Mul:
                case OpCode.FMul:
                case OpCode.Div:
                case OpCode.FDiv:
                case OpCode.Mod:
                case OpCode.FMod:
                case OpCode.Neg:
                case OpCode.FNeg:
                case OpCode.Inv:
                case OpCode.EQ:
                case OpCode.FEQ:
                case OpCode.REQ:
                case OpCode.StrEQ:
                case OpCode.DynEQ:
                case OpCode.NE:
                case OpCode.FNE:
                case OpCode.RNE:
                case OpCode.StrNE:
                case OpCode.DynNE:
                case OpCode.GT:
                case OpCode.FGT:
                case OpCode.GE:
                case OpCode.FGE:
                case OpCode.LT:
                case OpCode.FLT:
                case OpCode.LE:
                case OpCode.FLE:
                case OpCode.IsNull:
                case OpCode.IsNotNull:
                case OpCode.And:
                case OpCode.Or:
                case OpCode.Xor:
                case OpCode.Not:
                case OpCode.Shl:
                case OpCode.Shr:
                case OpCode.Sar:
                case OpCode.I2F:
                case OpCode.F2I:
                case OpCode.I2C:
                case OpCode.C2I:
                case OpCode.I2B:
                case OpCode.B2I:
                case OpCode.IsAbsent:
                case OpCode.IsPresent:
                case OpCode.RangeNew:
                case OpCode.RangeNewInclusive:
                case OpCode.RangePack:
                case OpCode.RangeUnpack:
                case OpCode.GenIterate:
                case OpCode.GenResume:
                case OpCode.GenCurrent:
                case OpCode.Yield:
                case OpCode.GenDelegate:
                case OpCode.GenResumed:
                case OpCode.BoxInt:
                case OpCode.BoxFloat:
                case OpCode.BoxBool:
                case OpCode.BoxChar:
                case OpCode.Unbox:
                case OpCode.BoxDynamic:
                case OpCode.UnboxDynamic:
                case OpCode.GetTypeOfValue:
                case OpCode.LoadCurrentModule:
                case OpCode.StrLen:
                case OpCode.StrHash:
                case OpCode.StrGet:
                case OpCode.ArrLen:
                case OpCode.ArrGet:
                case OpCode.ArrSet:
                case OpCode.ArrPush:
                case OpCode.ArrPop:
                case OpCode.ArrInsert:
                case OpCode.ArrRemoveAt:
                case OpCode.ArrClear:
                case OpCode.ArrIndexOf:
                case OpCode.ArrIn:
                case OpCode.TupLen:
                case OpCode.TupGet:
                case OpCode.DictLen:
                case OpCode.DictGet:
                case OpCode.DictSet:
                case OpCode.DictDel:
                case OpCode.DictClear:
                case OpCode.DictIn:
                case OpCode.Throw:
                case OpCode.ReturnVoid:
                case OpCode.ReturnValue:
                    builder.AppendLine();
                    return operand;

                // ---- one inline literal ------------------------------------------------------
                case OpCode.PushI8:
                    builder.Append(' ').Append((sbyte)chunk.Code[operand]).AppendLine();
                    return operand + 1;

                case OpCode.PushI16:
                    builder.Append(' ').Append((short)ReadU16(chunk, operand)).AppendLine();
                    return operand + 2;

                case OpCode.PushChar:
                    AppendCharLiteral(builder, (char)ReadU16(chunk, operand));
                    builder.AppendLine();
                    return operand + 2;

                case OpCode.PushI32:
                    builder.Append(' ').Append(ReadI32(chunk, operand)).AppendLine();
                    return operand + 4;

                // ---- constant pool -----------------------------------------------------------
                case OpCode.LdcS:
                    return AppendConstant(builder, chunk, operand, chunk.Code[operand], 1);

                case OpCode.Ldc:
                    return AppendConstant(builder, chunk, operand, ReadU16(chunk, operand), 2);

                // ---- frame slots and host globals --------------------------------------------
                case OpCode.LdlS:
                case OpCode.StlS:
                case OpCode.TupUnpack:
                case OpCode.TupGetC:
                case OpCode.UpValueGet:
                case OpCode.StrCat:
                case OpCode.PushAbsent:
                case OpCode.ReturnValues:
                case OpCode.UnboxValue:
                    builder.Append(' ').Append(chunk.Code[operand]).AppendLine();
                    return operand + 1;

                case OpCode.LoadValueLocal:
                case OpCode.StoreValueLocal:
                    builder.Append(' ').Append(ReadU16(chunk, operand))
                           .Append(" x").Append(chunk.Code[operand + 2]).AppendLine();
                    return operand + 3;

                case OpCode.LoadLocalField:
                case OpCode.StoreLocalField:
                    builder.Append(' ').Append(ReadU16(chunk, operand))
                           .Append('+').Append(ReadU16(chunk, operand + 2)).AppendLine();
                    return operand + 4;

                case OpCode.IncLocal:
                    builder.Append(' ').Append(chunk.Code[operand])
                           .Append(" by ").Append((sbyte)chunk.Code[operand + 1])
                           .AppendLine();
                    return operand + 2;

                case OpCode.Ldl:
                case OpCode.Stl:
                    builder.Append(' ').Append(ReadU16(chunk, operand)).AppendLine();
                    return operand + 2;

                // ---- type access table -------------------------------------------------------
                case OpCode.InstanceOf:
                case OpCode.Cast:
                case OpCode.CastOrNull:
                case OpCode.ArrNew:
                case OpCode.DictNew:
                case OpCode.DictKeys:
                case OpCode.DictValues:
                case OpCode.ObjNew:
                case OpCode.BoxAs:
                case OpCode.LoadType:
                    return AppendType(builder, chunk, operand, ReadU16(chunk, operand), 2);

                // ---- module access table -------------------------------------------------------
                case OpCode.LoadModule:
                    return AppendModule(builder, chunk, operand, ReadU16(chunk, operand), 2);

                case OpCode.ArrNewX:
                    AppendTypeName(builder, chunk, ReadU16(chunk, operand));
                    builder.Append(" size=").Append(ReadI32(chunk, operand + 2)).AppendLine();
                    return operand + 6;

                case OpCode.ArrPack:
                case OpCode.DictPack:
                    AppendTypeName(builder, chunk, ReadU16(chunk, operand));
                    builder.Append(" count=").Append(ReadU16(chunk, operand + 2)).AppendLine();
                    return operand + 4;

                case OpCode.TupPack:
                    AppendTypeName(builder, chunk, ReadU16(chunk, operand));
                    builder.Append(" count=").Append(chunk.Code[operand + 2]).AppendLine();
                    return operand + 3;

                case OpCode.BoxValue:
                    AppendTypeName(builder, chunk, ReadU16(chunk, operand));
                    builder.Append(" x").Append(chunk.Code[operand + 2]).AppendLine();
                    return operand + 3;

                // ---- field access table ------------------------------------------------------
                case OpCode.FieldGet:
                case OpCode.FieldSet:
                case OpCode.StaticFieldGet:
                case OpCode.StaticFieldSet:
                    return AppendFieldOperand(builder, chunk, operand, ReadU16(chunk, operand), 2);

                case OpCode.LoadValueField:
                case OpCode.StoreValueField:
                case OpCode.LoadValueStatic:
                case OpCode.StoreValueStatic:
                    AppendFieldName(builder, chunk, ReadU16(chunk, operand));
                    builder.Append(" x").Append(chunk.Code[operand + 2]).AppendLine();
                    return operand + 3;

                // ---- generators --------------------------------------------------------------
                case OpCode.GenNew:
                    AppendMethodName(builder, chunk, ReadU16(chunk, operand));
                    builder.Append(' ');
                    AppendTypeName(builder, chunk, ReadU16(chunk, operand + 2));
                    builder.Append(" args=").Append(chunk.Code[operand + 4]).AppendLine();
                    return operand + 5;

                // ---- closures ----------------------------------------------------------------
                case OpCode.NewClosure:
                    AppendMethodName(builder, chunk, ReadU16(chunk, operand));
                    builder.Append(" upvalues=").Append(chunk.Code[operand + 2]).AppendLine();
                    return operand + 3;

                case OpCode.NewFunction:
                    AppendMethodName(builder, chunk, ReadU16(chunk, operand));
                    builder.AppendLine();
                    return operand + 2;

                // ---- branches ----------------------------------------------------------------
                case OpCode.JPZ:
                case OpCode.JPNZ:
                case OpCode.JPN:
                case OpCode.JPNN:
                case OpCode.JPA:
                case OpCode.JPNA:
                case OpCode.JP:
                case OpCode.JPEQ:
                case OpCode.JPFEQ:
                case OpCode.JPREQ:
                case OpCode.JPStrEQ:
                case OpCode.JPNE:
                case OpCode.JPFNE:
                case OpCode.JPRNE:
                case OpCode.JPStrNE:
                case OpCode.JPGT:
                case OpCode.JPFGT:
                case OpCode.JPGE:
                case OpCode.JPFGE:
                case OpCode.JPLT:
                case OpCode.JPFLT:
                case OpCode.JPLE:
                case OpCode.JPFLE:
                    return AppendBranch(builder, chunk, operand, (short)ReadU16(chunk, operand), 2, jumpTargets, labels);

                    return AppendBranch(builder, chunk, operand, ReadI32(chunk, operand), 4, jumpTargets, labels);

                case OpCode.JPInstanceOf:
                {
                    AppendTypeName(builder, chunk, ReadU16(chunk, operand));
                    int target = operand + 4 + (short)ReadU16(chunk, operand + 2);
                    jumpTargets?.Add(target);
                    builder.Append(" -> ");
                    AppendTargetRef(builder, target, labels);
                    builder.AppendLine();
                    return operand + 4;
                }

                case OpCode.Switch:
                    return AppendSwitch(builder, chunk, position, operand, jumpTargets, labels);

                case OpCode.SwitchLookup:
                    return AppendSwitchLookup(builder, chunk, position, operand, jumpTargets, labels);

                // ---- calls -------------------------------------------------------------------
                case OpCode.CallLocalModule:
                case OpCode.InvokeVirtual:
                case OpCode.InvokeSpecial:
                case OpCode.InvokeStatic:
                case OpCode.InvokeInterface:
                    AppendMethodName(builder, chunk, ReadU16(chunk, operand));
                    AppendCounts(builder, chunk, operand + 2);
                    return operand + 4;

                case OpCode.CallModule:
                    AppendExternal(builder, chunk, ReadU16(chunk, operand), ReadU16(chunk, operand + 2));
                    AppendCounts(builder, chunk, operand + 4);
                    return operand + 6;

                case OpCode.InvokeClosure:
                    AppendCounts(builder, chunk, operand);
                    return operand + 2;

                default:
                    builder.Append("  ; unknown opcode 0x").Append(((byte)op).ToString("X2", CultureInfo.InvariantCulture)).AppendLine();
                    return operand;
            }
        }

        private static int AppendConstant(StringBuilder builder, SurtrChunk chunk, int operand, int index, int width)
        {
            builder.Append(' ').Append(index);

            bool isString = false;
            for (int i = 0; i < chunk.StringConstantSlots.Length; i++)
            {
                if (chunk.StringConstantSlots[i] == index)
                {
                    builder.Append(" \"").Append(chunk.StringConstants[i]).Append('"');
                    isString = true;
                    break;
                }
            }

            // Every non-string slot is a NaN-boxed SurtrValue: it already carries its own type
            // tag, so the constant pool needs no separate type table to decode it here.
            if (!isString)
                AppendTypedConstant(builder, chunk, index);

            builder.AppendLine();
            return operand + width;
        }

        private static void AppendTypedConstant(StringBuilder builder, SurtrChunk chunk, int index)
        {
            if ((uint)index >= (uint)chunk.Constants.Length)
                return;

            var value = SurtrValue.FromRaw(chunk.Constants[index]);

            if (value.IsInt)
                builder.Append(' ').Append(value.AsInt);
            else if (value.IsFloat)
                builder.Append(' ').Append(value.AsFloat.ToString("R", CultureInfo.InvariantCulture));
            else if (value.IsBool)
                builder.Append(' ').Append(value.AsBool ? "true" : "false");
            else if (value.IsChar)
                AppendCharLiteral(builder, value.AsChar);
            else if (value.IsAbsent)
                builder.Append(" <absent ").Append(value.AbsentTypeCode.ToString()).Append('>');
            else if (value.IsReference)
                builder.Append(value.IsNullReference ? " null" : " ref#" + value.AsReference);
        }

        private static void AppendCharLiteral(StringBuilder builder, char unit)
        {
            // A control character rendered as itself would corrupt the listing, so anything
            // outside printable ASCII is shown as its code unit instead.
            if (unit >= 0x20 && unit < 0x7F)
                builder.Append(" '").Append(unit).Append('\'');
            else
                builder.Append(" U+").Append(((int)unit).ToString("X4", CultureInfo.InvariantCulture));
        }

        private static int AppendType(StringBuilder builder, SurtrChunk chunk, int operand, int index, int width)
        {
            AppendTypeName(builder, chunk, index);
            builder.AppendLine();
            return operand + width;
        }

        private static void AppendTypeName(StringBuilder builder, SurtrChunk chunk, int index)
        {
            builder.Append(' ').Append(index);

            if ((uint)index < (uint)chunk.TypeTable.Length)
                builder.Append(" (").Append(chunk.TypeTable[index].Reference.ToDisplayString()).Append(')');
        }

        private static int AppendModule(StringBuilder builder, SurtrChunk chunk, int operand, int index, int width)
        {
            AppendModuleName(builder, chunk, index);
            builder.AppendLine();
            return operand + width;
        }

        private static void AppendModuleName(StringBuilder builder, SurtrChunk chunk, int index)
        {
            builder.Append(' ').Append(index);

            if ((uint)index < (uint)chunk.ModuleTable.Length && chunk.ModuleTable[index] is { } module)
                builder.Append(" (").Append(module.Path).Append(')');
        }

        private static int AppendFieldOperand(StringBuilder builder, SurtrChunk chunk, int operand, int index, int width)
        {
            builder.Append(' ').Append(index);

            if ((uint)index < (uint)chunk.FieldTable.Length && chunk.FieldTable[index] is { } field)
                builder.Append(" (").Append(field.Name).Append(')');

            builder.AppendLine();
            return operand + width;
        }

        private static void AppendFieldName(StringBuilder builder, SurtrChunk chunk, int index)
        {
            builder.Append(' ').Append(index);

            if ((uint)index < (uint)chunk.FieldTable.Length && chunk.FieldTable[index] is { } field)
                builder.Append(" (").Append(field.Name).Append(')');
        }

        private static void AppendMethodName(StringBuilder builder, SurtrChunk chunk, int index)
        {
            builder.Append(' ').Append(index);

            if ((uint)index < (uint)chunk.MethodTable.Length && chunk.MethodTable[index] is { } method)
                builder.Append(" (").Append(method.Name).Append(')');
        }

        private static void AppendExternal(StringBuilder builder, SurtrChunk chunk, int moduleIndex, int functionIndex)
        {
            builder.Append(' ').Append(moduleIndex).Append(':').Append(functionIndex);

            if ((uint)moduleIndex < (uint)chunk.ModuleTable.Length && chunk.ModuleTable[moduleIndex] is { } target)
            {
                builder.Append(" (").Append(target.Path);

                if ((uint)functionIndex < (uint)target.Chunk.MethodTable.Length && target.Chunk.MethodTable[functionIndex] is { } callee)
                    builder.Append('.').Append(callee.Name);

                builder.Append(')');
            }
        }

        private static void AppendCounts(StringBuilder builder, SurtrChunk chunk, int position)
        {
            builder.Append(" args=").Append(chunk.Code[position])
                   .Append(" ret=").Append(chunk.Code[position + 1])
                   .AppendLine();
        }

        private static int AppendBranch(
            StringBuilder builder,
            SurtrChunk chunk,
            int operand,
            int offset,
            int width,
            List<int>? jumpTargets,
            Dictionary<int, string>? labels)
        {
            int next = operand + width;
            int target = next + offset;
            jumpTargets?.Add(target);

            builder.Append(' ').Append(offset >= 0 ? "+" : string.Empty).Append(offset).Append(" -> ");
            AppendTargetRef(builder, target, labels);
            builder.AppendLine();
            return next;
        }

        /// <summary>Renders a branch target as its symbolic label when known, alongside its address either way.</summary>
        private static void AppendTargetRef(StringBuilder builder, int target, Dictionary<int, string>? labels)
        {
            if (labels != null && labels.TryGetValue(target, out string? label))
                builder.Append(label).Append(" (").Append(Hex(target)).Append(')');
            else
                builder.Append(Hex(target));
        }

        private static int AppendSwitch(
            StringBuilder builder,
            SurtrChunk chunk,
            int instruction,
            int operand,
            List<int>? jumpTargets,
            Dictionary<int, string>? labels)
        {
            int low = ReadI32(chunk, operand);
            int count = ReadI32(chunk, operand + 4);
            int defaultTarget = instruction + ReadI32(chunk, operand + 8);
            jumpTargets?.Add(defaultTarget);

            builder.Append(" low=").Append(low).Append(" count=").Append(count).Append(" default -> ");
            AppendTargetRef(builder, defaultTarget, labels);
            builder.AppendLine();

            for (int i = 0; i < count; i++)
            {
                int caseTarget = instruction + ReadI32(chunk, operand + 12 + (i * 4));
                jumpTargets?.Add(caseTarget);

                builder.Append("          case ").Append(low + i).Append(" -> ");
                AppendTargetRef(builder, caseTarget, labels);
                builder.AppendLine();
            }

            return operand + 12 + (count * 4);
        }

        private static int AppendSwitchLookup(
            StringBuilder builder,
            SurtrChunk chunk,
            int instruction,
            int operand,
            List<int>? jumpTargets,
            Dictionary<int, string>? labels)
        {
            int count = ReadI32(chunk, operand);
            int defaultTarget = instruction + ReadI32(chunk, operand + 4);
            jumpTargets?.Add(defaultTarget);

            builder.Append(" count=").Append(count).Append(" default -> ");
            AppendTargetRef(builder, defaultTarget, labels);
            builder.AppendLine();

            for (int i = 0; i < count; i++)
            {
                int entry = operand + 8 + (i * 8);
                int caseTarget = instruction + ReadI32(chunk, entry + 4);
                jumpTargets?.Add(caseTarget);

                builder.Append("          case ").Append(ReadI32(chunk, entry)).Append(" -> ");
                AppendTargetRef(builder, caseTarget, labels);
                builder.AppendLine();
            }

            return operand + 8 + (count * 8);
        }

        /// <summary>Decodes one instruction from the extended space, prefix included.</summary>
        /// <remarks>
        /// <paramref name="position"/> is the prefix byte; the sub-opcode is the next one and its
        /// immediates start two bytes in. The returned position is past the whole instruction.
        /// </remarks>
        /// <summary>
        /// Renders one instruction behind <see cref="OpCode.Wide"/>: the same opcode the ordinary
        /// decoder knows, with its single index or offset read as four bytes.
        /// </summary>
        /// <remarks>
        /// Written as its own method rather than as a width flag threaded through the ordinary
        /// decoder, for the reason the extended space has one too: the widened forms are a small,
        /// closed set of six shapes, and forty unrelated cases would otherwise each have to carry
        /// a test for a width they can never have.
        /// </remarks>
        private static int AppendWideInstruction(
            StringBuilder builder,
            SurtrChunk chunk,
            int position,
            List<int>? jumpTargets,
            Dictionary<int, string>? labels,
            string indent)
        {
            var op = (OpCode)chunk.Code[position + 1];
            int operand = position + 2;

            builder.Append(indent).Append("  ").Append(Hex(position)).Append("  Wide ").Append(op.ToString());

            switch (op)
            {
                case OpCode.Ldc:
                    return AppendConstant(builder, chunk, operand, ReadI32(chunk, operand), 4);

                case OpCode.InstanceOf:
                case OpCode.Cast:
                case OpCode.CastOrNull:
                case OpCode.ObjNew:
                case OpCode.BoxAs:
                case OpCode.LoadType:
                    return AppendType(builder, chunk, operand, ReadI32(chunk, operand), 4);

                case OpCode.LoadModule:
                    return AppendModule(builder, chunk, operand, ReadI32(chunk, operand), 4);

                case OpCode.StaticFieldGet:
                case OpCode.StaticFieldSet:
                    return AppendFieldOperand(builder, chunk, operand, ReadI32(chunk, operand), 4);

                case OpCode.NewClosure:
                    AppendMethodName(builder, chunk, ReadI32(chunk, operand));
                    builder.Append(" upvalues=").Append(chunk.Code[operand + 4]).AppendLine();
                    return operand + 5;

                case OpCode.NewFunction:
                    AppendMethodName(builder, chunk, ReadI32(chunk, operand));
                    builder.AppendLine();
                    return operand + 4;

                case OpCode.JPInstanceOf:
                {
                    AppendTypeName(builder, chunk, ReadI32(chunk, operand));
                    int target = operand + 8 + ReadI32(chunk, operand + 4);
                    jumpTargets?.Add(target);
                    builder.Append(" -> ");
                    AppendTargetRef(builder, target, labels);
                    builder.AppendLine();
                    return operand + 8;
                }

                case OpCode.CallLocalModule:
                case OpCode.InvokeStatic:
                    AppendMethodName(builder, chunk, ReadI32(chunk, operand));
                    AppendCounts(builder, chunk, operand + 4);
                    return operand + 6;

                case OpCode.CallModule:
                    AppendExternal(builder, chunk, ReadI32(chunk, operand), ReadI32(chunk, operand + 4));
                    AppendCounts(builder, chunk, operand + 8);
                    return operand + 10;

                case OpCode.JP:
                case OpCode.JPZ:
                case OpCode.JPNZ:
                case OpCode.JPN:
                case OpCode.JPNN:
                case OpCode.JPA:
                case OpCode.JPNA:
                case OpCode.JPEQ:
                case OpCode.JPNE:
                case OpCode.JPGT:
                case OpCode.JPGE:
                case OpCode.JPLT:
                case OpCode.JPLE:
                case OpCode.JPFEQ:
                case OpCode.JPFNE:
                case OpCode.JPFGT:
                case OpCode.JPFGE:
                case OpCode.JPFLT:
                case OpCode.JPFLE:
                case OpCode.JPREQ:
                case OpCode.JPRNE:
                case OpCode.JPStrEQ:
                case OpCode.JPStrNE:
                {
                    int target = operand + 4 + ReadI32(chunk, operand);
                    jumpTargets?.Add(target);
                    builder.Append(' ').Append(ReadI32(chunk, operand)).Append(" -> ");
                    AppendTargetRef(builder, target, labels);
                    builder.AppendLine();
                    return operand + 4;
                }

                default:
                    builder.Append("  ; opcode 0x")
                        .Append(((byte)op).ToString("X2", CultureInfo.InvariantCulture))
                        .Append(" has no wide form").AppendLine();
                    return operand;
            }
        }

        private static int AppendExtInstruction(
            StringBuilder builder,
            SurtrChunk chunk,
            int position,
            List<int>? jumpTargets,
            Dictionary<int, string>? labels,
            string indent)
        {
            var op = (SurtrExtOpCode)chunk.Code[position + 1];
            int operand = position + 2;

            builder.Append(indent).Append("  ").Append(Hex(position)).Append("  ").Append(op.ToString());

            switch (op)
            {
                case SurtrExtOpCode.Probe:
                    builder.Append(' ').Append(chunk.Code[operand]).AppendLine();
                    return operand + 1;

                // ---- loop steps: slot operands, then a branch back into the body -------------
                case SurtrExtOpCode.ArrForNext:
                case SurtrExtOpCode.StrForNext:
                case SurtrExtOpCode.TupForNext:
                    return AppendExtLoopStep(builder, chunk, operand, slots: 3, offsetWidth: 2, jumpTargets, labels);

                case SurtrExtOpCode.ArrForNextX:
                case SurtrExtOpCode.StrForNextX:
                case SurtrExtOpCode.TupForNextX:
                    return AppendExtLoopStep(builder, chunk, operand, slots: 3, offsetWidth: 4, jumpTargets, labels);

                case SurtrExtOpCode.DictForNext:
                    return AppendExtLoopStep(builder, chunk, operand, slots: 4, offsetWidth: 2, jumpTargets, labels);

                case SurtrExtOpCode.DictForNextX:
                    return AppendExtLoopStep(builder, chunk, operand, slots: 4, offsetWidth: 4, jumpTargets, labels);

                case SurtrExtOpCode.ForRangeNextLE:
                case SurtrExtOpCode.ForRangeNextLT:
                    return AppendExtLoopStep(builder, chunk, operand, slots: 2, offsetWidth: 2, jumpTargets, labels);

                case SurtrExtOpCode.ForRangeNextLEX:
                case SurtrExtOpCode.ForRangeNextLTX:
                    return AppendExtLoopStep(builder, chunk, operand, slots: 2, offsetWidth: 4, jumpTargets, labels);

                default:
                    builder.Append(" ; unknown extended opcode 0x")
                        .Append(((byte)op).ToString("X2", CultureInfo.InvariantCulture))
                        .AppendLine();
                    return operand;
            }
        }

        /// <summary>Renders a loop step's slot operands and the branch back into the body.</summary>
        /// <remarks>
        /// One shape covers the whole family: <paramref name="slots"/> single-byte frame indices,
        /// then a signed offset of <paramref name="offsetWidth"/> bytes measured from the end of
        /// the instruction, exactly like every branch outside <c>Switch</c>.
        /// </remarks>
        private static int AppendExtLoopStep(
            StringBuilder builder,
            SurtrChunk chunk,
            int operand,
            int slots,
            int offsetWidth,
            List<int>? jumpTargets,
            Dictionary<int, string>? labels)
        {
            for (int i = 0; i < slots; i++)
                builder.Append(' ').Append(chunk.Code[operand + i]);

            int offsetAt = operand + slots;
            int offset = offsetWidth == 2
                ? (short)ReadU16(chunk, offsetAt)
                : ReadI32(chunk, offsetAt);

            int end = offsetAt + offsetWidth;
            int target = end + offset;

            jumpTargets?.Add(target);
            builder.Append(" -> ");
            AppendTargetRef(builder, target, labels);
            builder.AppendLine();

            return end;
        }

        private static int ReadU16(SurtrChunk chunk, int position)
            => chunk.Code[position] | (chunk.Code[position + 1] << 8);

        private static int ReadI32(SurtrChunk chunk, int position)
            => chunk.Code[position]
             | (chunk.Code[position + 1] << 8)
             | (chunk.Code[position + 2] << 16)
             | (chunk.Code[position + 3] << 24);

        private static string Hex(int offset) => offset.ToString("X4", CultureInfo.InvariantCulture);

        #endregion
    }
}
