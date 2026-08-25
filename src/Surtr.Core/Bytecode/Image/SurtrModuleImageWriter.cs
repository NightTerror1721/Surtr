#nullable enable

using Surtr.Runtime.Classes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Surtr.Bytecode.Image
{
    /// <summary>
    /// Writes a built <see cref="SurtrModule"/> out as bytes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The layout is: header, string table, then the module - its chunk first, then everything it
    /// declares. Every name, descriptor and literal goes through
    /// <see cref="Intern"/> and travels as an index, which is what keeps a format this
    /// descriptor-heavy small: <c>Osurtr:Exception;</c> is written once no matter how many members
    /// mention it.
    /// </para>
    /// <para>
    /// The body is built first and the string table written in front of it, because the table is
    /// only complete once the body has been walked. Nothing here is on an execution path, so it
    /// favours being obviously correct over being allocation-free.
    /// </para>
    /// </remarks>
    internal static class SurtrModuleImageWriter
    {
        /// <summary>Marks a member reference whose owner is a type, named by its descriptor.</summary>
        internal const byte OwnerType = 0;

        /// <summary>Marks a member reference whose owner is the module being written.</summary>
        internal const byte OwnerModule = 1;

        /// <summary>Stands in for an absent index - a catch-all handler's type, a class with no base.</summary>
        internal const int NoIndex = -1;

        internal static byte[] Write(SurtrModule module)
        {
            if (ReferenceEquals(module, Runtime.BuiltIns.SurtrBuiltIns.Module))
                throw new ArgumentException(
                    "The built-in module is process-wide and shared by every runtime; a second copy of it would shadow the real one rather than extend it.",
                    nameof(module));

            if (!module.IsEmitted)
                throw new ArgumentException(
                    $"Module '{module.Path}' has no emitted bodies; call SurtrModuleBuilder.Build() before writing an image.",
                    nameof(module));

            var strings = new List<string>();
            var stringIndices = new Dictionary<string, int>(StringComparer.Ordinal);

            using var body = new MemoryStream(module.Chunk.Code.Length + 4096);
            using (var writer = new BinaryWriter(body, Encoding.UTF8, leaveOpen: true))
            {
                var state = new WriterState(writer, strings, stringIndices, module);

                writer.Write(Intern(state, module.Path));
                WriteChunk(state, module.Chunk);
                WriteModuleDeclarations(state, module);
            }

            using var image = new MemoryStream((int)body.Length + 1024);
            using (var writer = new BinaryWriter(image, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(SurtrModuleImage.Magic);
                writer.Write(SurtrModuleImage.FormatVersion);

                writer.Write(strings.Count);
                for (int i = 0; i < strings.Count; i++)
                {
                    byte[] utf8 = Encoding.UTF8.GetBytes(strings[i]);
                    writer.Write(utf8.Length);
                    writer.Write(utf8);
                }

                writer.Write(body.ToArray());
            }

            return image.ToArray();
        }

        private readonly struct WriterState
        {
            internal readonly BinaryWriter Writer;
            internal readonly List<string> Strings;
            internal readonly Dictionary<string, int> Indices;
            internal readonly SurtrModule Module;

            internal WriterState(BinaryWriter writer, List<string> strings, Dictionary<string, int> indices, SurtrModule module)
            {
                Writer = writer;
                Strings = strings;
                Indices = indices;
                Module = module;
            }
        }

        private static int Intern(in WriterState state, string text)
        {
            if (state.Indices.TryGetValue(text, out int existing))
                return existing;

            int index = state.Strings.Count;
            state.Strings.Add(text);
            state.Indices.Add(text, index);
            return index;
        }

        #region Chunk

        private static void WriteChunk(in WriterState state, SurtrChunk chunk)
        {
            var writer = state.Writer;

            // One memcpy into a managed array, then one Write: a module's code is the single
            // largest contiguous span in the image, and writing it byte by byte is a virtual call
            // per byte.
            var code = chunk.Code;
            var codeBytes = new byte[code.Length];
            code.CopyTo(codeBytes, 0, code.Length);
            writer.Write(code.Length);
            writer.Write(codeBytes);

            writer.Write(chunk.Constants.Length);
            for (int i = 0; i < chunk.Constants.Length; i++)
                writer.Write(chunk.Constants[i]);

            writer.Write(chunk.MethodOffsets.Length);
            for (int i = 0; i < chunk.MethodOffsets.Length; i++)
                writer.Write(chunk.MethodOffsets[i]);

            // The literal's text plus the pool slot it is patched into at load. The slot's current
            // contents are deliberately not written: they are a reference into the heap of whichever
            // runtime last loaded this module, and mean nothing anywhere else.
            writer.Write(chunk.StringConstants.Length);
            for (int i = 0; i < chunk.StringConstants.Length; i++)
            {
                writer.Write(Intern(state, chunk.StringConstants[i]));
                writer.Write(chunk.StringConstantSlots[i]);
            }

            writer.Write(chunk.TypeTable.Length);
            for (int i = 0; i < chunk.TypeTable.Length; i++)
                writer.Write(Intern(state, chunk.TypeTable[i].Reference.Descriptor));

            // Everything below is written by *name*, so a module that came from an image and has
            // not been loaded yet is written straight back out of the names it is still carrying.
            // Only a load turns those into objects, and re-serializing is a thing build tools do.

            // By path, not by instance: the module a call lands in is whichever one the loading
            // runtime has under that path, which is the whole point of the reference table.
            if (chunk.PendingModulePaths.Length != 0)
            {
                WriteNameList(state, chunk.PendingModulePaths);
            }
            else
            {
                writer.Write(chunk.ModuleTable.Length);
                for (int i = 0; i < chunk.ModuleTable.Length; i++)
                    writer.Write(Intern(state, chunk.ModuleTable[i].Path));
            }

            if (chunk.PendingFields.Length != 0)
            {
                writer.Write(chunk.PendingFields.Length);
                for (int i = 0; i < chunk.PendingFields.Length; i++)
                    WritePendingMember(state, chunk.PendingFields[i], withSignature: false);
            }
            else
            {
                writer.Write(chunk.FieldTable.Length);
                for (int i = 0; i < chunk.FieldTable.Length; i++)
                    WriteMemberReference(state, chunk.FieldTable[i], signatureKey: null);
            }

            if (chunk.PendingMethods.Length != 0)
            {
                writer.Write(chunk.PendingMethods.Length);
                for (int i = 0; i < chunk.PendingMethods.Length; i++)
                    WritePendingMember(state, chunk.PendingMethods[i], withSignature: true);
            }
            else
            {
                writer.Write(chunk.MethodTable.Length);
                for (int i = 0; i < chunk.MethodTable.Length; i++)
                {
                    var method = chunk.MethodTable[i];
                    WriteMemberReference(state, method, method.SignatureKey());
                }
            }
        }

        private static void WritePendingMember(in WriterState state, in SurtrPendingMember pending, bool withSignature)
        {
            if (pending.OwnerDescriptor is null)
            {
                state.Writer.Write(OwnerModule);
            }
            else
            {
                state.Writer.Write(OwnerType);
                state.Writer.Write(Intern(state, pending.OwnerDescriptor));
            }

            state.Writer.Write(Intern(state, pending.Name));

            if (withSignature)
                state.Writer.Write(Intern(state, pending.SignatureKey!));
        }

        private static void WriteNameList(in WriterState state, string[] names)
        {
            state.Writer.Write(names.Length);
            for (int i = 0; i < names.Length; i++)
                state.Writer.Write(Intern(state, names[i]));
        }

        /// <summary>
        /// Writes an access-table entry as the name of what it points at, rather than as a link.
        /// </summary>
        /// <remarks>
        /// A member declared in a class is named by its declaring type's descriptor, which carries
        /// the module path with it - so an entry pointing into another module, or into a built-in,
        /// travels fine and is bound when the module loads.
        /// </remarks>
        private static void WriteMemberReference(in WriterState state, SurtrMemberInfo member, string? signatureKey)
        {
            var declaringType = member.DeclaringType;

            if (declaringType is null)
            {
                // Nothing on a module-level member records which module declares it, so the only
                // one that can be named is the module being written. A cross-module *call* is
                // unaffected: those go through the module reference table by path.
                if (!DeclaredHere(state.Module, member))
                    throw new ArgumentException(
                        $"Module '{state.Module.Path}' names module-level member '{member.Name}', which it does not declare. " +
                        "A module-level member of another module cannot be named in an access table; call it through the module reference table instead.",
                        nameof(member));

                state.Writer.Write(OwnerModule);
            }
            else
            {
                state.Writer.Write(OwnerType);
                state.Writer.Write(Intern(state, declaringType.Reference.Descriptor));
            }

            state.Writer.Write(Intern(state, member.Name));

            if (signatureKey is not null)
                state.Writer.Write(Intern(state, signatureKey));
        }

        private static bool DeclaredHere(SurtrModule module, SurtrMemberInfo member)
        {
            if (member is SurtrFieldInfo)
                return module.TryGetField(member.Name, out var field) && ReferenceEquals(field, member);

            if (!module.TryGetMethods(member.Name, out var overloads))
                return false;

            for (int i = 0; i < overloads.Length; i++)
            {
                if (ReferenceEquals(overloads[i], member))
                    return true;
            }

            return false;
        }

        #endregion

        #region Declarations

        private static void WriteModuleDeclarations(in WriterState state, SurtrModule module)
        {
            var writer = state.Writer;

            WriteCount(writer, module.Fields.Count);
            foreach (var field in module.Fields)
                WriteField(state, field);

            WriteCount(writer, module.Properties.Count);
            foreach (var property in module.Properties)
                WriteProperty(state, property);

            int methodCount = 0;
            foreach (var overloads in module.Methods)
                methodCount += overloads.Length;

            WriteCount(writer, methodCount);
            foreach (var overloads in module.Methods)
            {
                for (int i = 0; i < overloads.Length; i++)
                    WriteMethod(state, overloads[i]);
            }

            WriteCount(writer, module.Classes.Count);
            foreach (var type in module.Classes)
                WriteClass(state, type);

            WriteCount(writer, module.Interfaces.Count);
            foreach (var contract in module.Interfaces)
                WriteInterface(state, contract);
        }

        private static void WriteCount(BinaryWriter writer, int count) => writer.Write(count);

        private static void WriteField(in WriterState state, SurtrFieldInfo field)
        {
            var writer = state.Writer;

            writer.Write(Intern(state, field.Name));
            writer.Write(Intern(state, field.FieldType.Reference.Descriptor));
            writer.Write(field.IsStatic);
            writer.Write(field.IsReadOnly);
            writer.Write((byte)field.Visibility);

            WriteAttributes(state, field);
        }

        private static void WriteProperty(in WriterState state, SurtrPropertyInfo property)
        {
            var writer = state.Writer;

            writer.Write(Intern(state, property.Name));
            writer.Write(Intern(state, property.PropertyType.Reference.Descriptor));
            writer.Write(property.IsStatic);
            writer.Write((byte)property.Visibility);

            // The accessors are ordinary methods of the same declaring type, so a signature key is
            // enough to find them again once that type's methods have been rebuilt.
            WriteAccessor(state, property.Getter);
            WriteAccessor(state, property.Setter);

            WriteAttributes(state, property);
        }

        private static void WriteAccessor(in WriterState state, SurtrMethodInfo? accessor)
        {
            if (accessor is null)
            {
                state.Writer.Write(false);
                return;
            }

            state.Writer.Write(true);
            state.Writer.Write(Intern(state, accessor.SignatureKey()));
        }

        private static void WriteMethod(in WriterState state, SurtrMethodInfo method)
        {
            var writer = state.Writer;

            writer.Write(Intern(state, method.Name));
            writer.Write(Intern(state, method.ReturnType.Reference.Descriptor));
            writer.Write((byte)method.ImplKind);
            writer.Write((byte)method.Dispatch);
            writer.Write((byte)method.Role);
            writer.Write((byte)method.Visibility);
            writer.Write(method.IsStatic);
            writer.Write(method.IsOverride);
            writer.Write(method.IsSealed);
            writer.Write(method.IsExtension);

            var parameters = method.Parameters;
            writer.Write(parameters.Length);
            for (int i = 0; i < parameters.Length; i++)
                WriteParameter(state, parameters[i]);

            // The method's own generic parameters, names then per-parameter constraint lists -
            // the same shape the type sections carry, so a reader has one rule to know.
            var genericParameters = method.GenericParameters;
            writer.Write(genericParameters.Count);
            for (int i = 0; i < genericParameters.Count; i++)
                writer.Write(Intern(state, genericParameters[i]));

            if (genericParameters.Count > 0)
            {
                var constraints = method.GenericConstraints;
                for (int i = 0; i < genericParameters.Count; i++)
                {
                    var bounds = constraints[i];
                    writer.Write(bounds.Length);
                    for (int b = 0; b < bounds.Length; b++)
                        writer.Write(Intern(state, bounds[b]));
                }
            }

            if (method is SurtrBytecodeMethodInfo bytecode)
            {
                writer.Write(bytecode.EntryIndex);
                writer.Write(bytecode.LocalCount);
                writer.Write(bytecode.MaxStackSize);

                var handlers = bytecode.Handlers;
                writer.Write(handlers.Length);
                for (int i = 0; i < handlers.Length; i++)
                    WriteHandler(state, handlers[i]);
            }
            else if (method is SurtrNativeMethodInfo native)
            {
                // The name, never the address. A body is a pointer into the process that declared
                // it, and the whole point of an image is to be read by a different one - so what
                // travels is what the loading runtime can look up for itself.
                writer.Write(Intern(state, native.LinkName));
            }

            WriteAttributes(state, method);
        }

        private static void WriteParameter(in WriterState state, in SurtrParameterInfo parameter)
        {
            state.Writer.Write(Intern(state, parameter.Name));
            state.Writer.Write(Intern(state, parameter.ParameterType.Reference.Descriptor));
            state.Writer.Write(parameter.IsVarargs);

            WriteConstant(state, parameter.DefaultValue);
        }

        private static void WriteHandler(in WriterState state, in SurtrExceptionHandler handler)
        {
            var writer = state.Writer;

            writer.Write(handler.TryStart);
            writer.Write(handler.TryEnd);
            writer.Write(handler.HandlerOffset);

            var catchType = handler.CatchType;
            writer.Write(catchType is null ? NoIndex : Intern(state, catchType.Reference.Descriptor));
        }

        private static void WriteConstant(in WriterState state, in SurtrConstant constant)
        {
            var writer = state.Writer;
            writer.Write((byte)constant.Kind);

            switch (constant.Kind)
            {
                case SurtrConstantKind.None:
                case SurtrConstantKind.Null:
                    return;

                case SurtrConstantKind.String:
                    writer.Write(Intern(state, constant.Text!));
                    return;

                default:
                    // Every other kind is a primitive, and its encoded bits are the whole value.
                    writer.Write(constant.Value.Raw);
                    return;
            }
        }

        private static void WriteAttributes(in WriterState state, SurtrMemberInfo member)
        {
            var attributes = member.Attributes;
            state.Writer.Write(attributes.Length);

            for (int i = 0; i < attributes.Length; i++)
            {
                var usage = attributes[i];
                state.Writer.Write(Intern(state, usage.AttributeType.Reference.Descriptor));

                var arguments = usage.Arguments;
                state.Writer.Write(arguments.Length);
                for (int a = 0; a < arguments.Length; a++)
                    WriteConstant(state, arguments[a]);
            }
        }

        private static void WriteClass(in WriterState state, SurtrClass type)
        {
            var writer = state.Writer;

            writer.Write(Intern(state, type.Name));
            writer.Write((byte)type.TypeCode);
            writer.Write((byte)type.Visibility);
            writer.Write(type.IsAbstract);
            writer.Write(type.IsSealed);
            writer.Write(type.IsEnum);
            writer.Write(type.IsValueType);

            writer.Write(type.BaseType is null ? NoIndex : Intern(state, type.BaseType.Reference.Descriptor));

            // A type carries attributes exactly as a member does - `SurtrTypeInfo` extends
            // `SurtrMemberInfo` - and §11 decorates a class as readily as a field. Without this an
            // attribute on a class would work in the process that compiled it and vanish through an
            // image, which is the worst of both.
            WriteAttributes(state, type);

            writer.Write(type.DeclaredInterfaces.Length);
            for (int i = 0; i < type.DeclaredInterfaces.Length; i++)
                writer.Write(Intern(state, type.DeclaredInterfaces[i].Reference.Descriptor));

            var genericParameters = type.GenericParameters;
            writer.Write(genericParameters.Length);
            for (int i = 0; i < genericParameters.Length; i++)
                writer.Write(Intern(state, genericParameters[i]));

            // One variance byte per parameter - out/in/invariant, §6's declaration-site annotation.
            // Like the constraint table below it, nothing on an execution path reads it back; it
            // exists so an imported construction answers subtype questions without its source.
            var genericVariance = type.GenericVariance;
            for (int i = 0; i < genericParameters.Length; i++)
                writer.Write(i < genericVariance.Length ? (byte)genericVariance[i] : (byte)SurtrGenericVariance.Invariant);

            // The constraints ride along with the parameters that declare them, as descriptor
            // strings naming the bound - `G<n>` included, so a bound naming the type's own
            // parameter means the same thing after the round trip. Nothing on an execution path
            // reads them; they exist for the importer, tooling and host interop.
            var genericConstraints = type.GenericConstraints;
            for (int i = 0; i < genericConstraints.Length; i++)
            {
                var constraints = genericConstraints[i];
                writer.Write(constraints.Length);
                for (int j = 0; j < constraints.Length; j++)
                    writer.Write(Intern(state, constraints[j]));
            }

            // Enum cases come before the fields so the reader can register each case's backing
            // field through AddEnumCase, which is what assigns the ordinal - writing the ordinal
            // and trusting it would let a hand-edited image renumber a switch.
            var cases = type.EnumCases;
            writer.Write(cases.Length);
            for (int i = 0; i < cases.Length; i++)
            {
                writer.Write(Intern(state, cases[i].Name));
                writer.Write((byte)cases[i].Field.Visibility);
            }

            WriteCount(writer, CountFieldsExcludingCases(type));
            foreach (var field in type.Fields)
            {
                if (!IsEnumCaseField(type, field))
                    WriteField(state, field);
            }

            WriteCount(writer, type.Properties.Count);
            foreach (var property in type.Properties)
                WriteProperty(state, property);

            int methodCount = 0;
            foreach (var overloads in type.Methods)
                methodCount += overloads.Length;

            WriteCount(writer, methodCount);
            foreach (var overloads in type.Methods)
            {
                for (int i = 0; i < overloads.Length; i++)
                    WriteMethod(state, overloads[i]);
            }

            WriteCount(writer, type.NestedClasses.Count);
            foreach (var nested in type.NestedClasses)
                WriteClass(state, nested);

            WriteCount(writer, type.NestedInterfaces.Count);
            foreach (var nested in type.NestedInterfaces)
                WriteInterface(state, nested);
        }

        private static int CountFieldsExcludingCases(SurtrClass type)
        {
            int count = 0;
            foreach (var field in type.Fields)
            {
                if (!IsEnumCaseField(type, field))
                    count++;
            }

            return count;
        }

        private static bool IsEnumCaseField(SurtrClass type, SurtrFieldInfo field)
        {
            var cases = type.EnumCases;
            for (int i = 0; i < cases.Length; i++)
            {
                if (ReferenceEquals(cases[i].Field, field))
                    return true;
            }

            return false;
        }

        private static void WriteInterface(in WriterState state, SurtrInterface contract)
        {
            var writer = state.Writer;

            writer.Write(Intern(state, contract.Name));
            writer.Write((byte)contract.Visibility);

            WriteAttributes(state, contract);

            writer.Write(contract.DeclaredExtendedInterfaces.Length);
            for (int i = 0; i < contract.DeclaredExtendedInterfaces.Length; i++)
                writer.Write(Intern(state, contract.DeclaredExtendedInterfaces[i].Reference.Descriptor));

            var genericParameters = contract.GenericParameters;
            writer.Write(genericParameters.Length);
            for (int i = 0; i < genericParameters.Length; i++)
                writer.Write(Intern(state, genericParameters[i]));

            // One variance byte per parameter, exactly as a class writes - and doubly worth
            // carrying here, since out/in is first of all a contract's promise about its element.
            var genericVariance = contract.GenericVariance;
            for (int i = 0; i < genericParameters.Length; i++)
                writer.Write(i < genericVariance.Length ? (byte)genericVariance[i] : (byte)SurtrGenericVariance.Invariant);

            var genericConstraints = contract.GenericConstraints;
            for (int i = 0; i < genericConstraints.Length; i++)
            {
                var constraints = genericConstraints[i];
                writer.Write(constraints.Length);
                for (int j = 0; j < constraints.Length; j++)
                    writer.Write(Intern(state, constraints[j]));
            }

            int methodCount = 0;
            foreach (var overloads in contract.Methods)
                methodCount += overloads.Length;

            WriteCount(writer, methodCount);
            foreach (var overloads in contract.Methods)
            {
                for (int i = 0; i < overloads.Length; i++)
                    WriteMethod(state, overloads[i]);
            }

            WriteCount(writer, contract.Properties.Count);
            foreach (var property in contract.Properties)
                WriteProperty(state, property);
        }

        #endregion
    }
}
