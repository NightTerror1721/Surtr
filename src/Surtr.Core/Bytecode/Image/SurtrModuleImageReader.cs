#nullable enable

using Surtr.Runtime.Classes;
using Surtr.Runtime.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Surtr.Bytecode.Image
{
    /// <summary>
    /// Rebuilds a <see cref="SurtrModule"/> from the bytes
    /// <see cref="SurtrModuleImageWriter"/> produced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything the module declares is rebuilt here and now, because none of it depends on a
    /// runtime: a class, its layout-free metadata and its bodies mean the same thing wherever they
    /// are loaded. Everything that names something <em>outside</em> the module is left as text for
    /// the load to bind - see <see cref="SurtrPendingMember"/>.
    /// </para>
    /// <para>
    /// The reader is deliberately strict. An image is machine-written, so anything that does not
    /// parse is a corrupt file or a version mismatch, and guessing at either produces a module that
    /// fails much later and much less clearly.
    /// </para>
    /// </remarks>
    internal static class SurtrModuleImageReader
    {
        /// <summary>Reads just the module path, to answer what an image holds without rebuilding it.</summary>
        internal static string ReadPath(byte[] bytes)
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8);

            string[] strings = ReadHeader(reader);
            return strings[ReadStringIndex(reader, strings)];
        }

        internal static SurtrModule Read(byte[] bytes)
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8);

            string[] strings = ReadHeader(reader);

            var module = new SurtrModule(strings[ReadStringIndex(reader, strings)]);
            var state = new ReaderState(reader, strings, module);

            try
            {
                ReadChunk(state);
                ReadModuleDeclarations(state);
            }
            catch (EndOfStreamException)
            {
                module.Dispose();
                throw new SurtrImageFormatException("The image ends part-way through a module; the bytes are truncated.");
            }
            catch
            {
                // A half-built module owns unmanaged buffers nobody else has a reference to.
                module.Dispose();
                throw;
            }

            module.MarkEmitted();
            return module;
        }

        private static string[] ReadHeader(BinaryReader reader)
        {
            ulong magic;
            ushort version;

            try
            {
                magic = reader.ReadUInt64();
                version = reader.ReadUInt16();
            }
            catch (EndOfStreamException)
            {
                throw new SurtrImageFormatException("The image is too short to hold a header.");
            }

            if (magic != SurtrModuleImage.Magic)
                throw new SurtrImageFormatException("These bytes do not start with a Surtr module image header.");

            if (version != SurtrModuleImage.FormatVersion)
                throw new SurtrImageFormatException(
                    $"The image is format version {version}; this build reads version {SurtrModuleImage.FormatVersion}.");

            int count = reader.ReadInt32();
            if (count < 0)
                throw new SurtrImageFormatException("The image declares a negative string count.");

            var strings = new string[count];
            for (int i = 0; i < count; i++)
            {
                int length = reader.ReadInt32();
                if (length < 0)
                    throw new SurtrImageFormatException("The image declares a string of negative length.");

                strings[i] = Encoding.UTF8.GetString(reader.ReadBytes(length));
            }

            return strings;
        }

        private static int ReadStringIndex(BinaryReader reader, string[] strings)
        {
            int index = reader.ReadInt32();
            if ((uint)index >= (uint)strings.Length)
                throw new SurtrImageFormatException($"The image refers to string {index}, which its table does not hold.");

            return index;
        }

        private sealed class ReaderState
        {
            internal readonly BinaryReader Reader;
            internal readonly string[] Strings;
            internal readonly SurtrModule Module;

            internal ReaderState(BinaryReader reader, string[] strings, SurtrModule module)
            {
                Reader = reader;
                Strings = strings;
                Module = module;
            }

            internal string Text() => Strings[ReadStringIndex(Reader, Strings)];

            /// <summary>Reads an index that may be <see cref="SurtrModuleImageWriter.NoIndex"/>.</summary>
            internal string? OptionalText()
            {
                int index = Reader.ReadInt32();
                if (index == SurtrModuleImageWriter.NoIndex)
                    return null;

                if ((uint)index >= (uint)Strings.Length)
                    throw new SurtrImageFormatException($"The image refers to string {index}, which its table does not hold.");

                return Strings[index];
            }

            internal SurtrTypeHandle Handle(string descriptor)
                => Module.TypeHandles.GetOrAdd(SurtrClassReference.FromDescriptor(descriptor));

            internal SurtrTypeHandle HandleOf(string? descriptor)
                => descriptor is null ? null! : Handle(descriptor);

            internal int Count()
            {
                int count = Reader.ReadInt32();
                if (count < 0)
                    throw new SurtrImageFormatException("The image declares a negative element count.");

                return count;
            }
        }

        #region Chunk

        private static void ReadChunk(ReaderState state)
        {
            var reader = state.Reader;
            var chunk = state.Module.Chunk;

            int codeLength = state.Count();
            chunk.Code = new SurtrNativeArray<byte>(codeLength);
            for (int i = 0; i < codeLength; i++)
                chunk.Code[i] = reader.ReadByte();

            int constantCount = state.Count();
            chunk.Constants = new SurtrNativeArray<SurtrRawValue>(constantCount);
            for (int i = 0; i < constantCount; i++)
                chunk.Constants[i] = reader.ReadUInt64();

            // Read before any method metadata is built: SurtrBytecodeMethodInfo snapshots its own
            // offset out of this table in its constructor.
            int offsetCount = state.Count();
            chunk.MethodOffsets = new SurtrNativeArray<int>(offsetCount);
            for (int i = 0; i < offsetCount; i++)
                chunk.MethodOffsets[i] = reader.ReadInt32();

            int literalCount = state.Count();
            chunk.StringConstants = new string[literalCount];
            chunk.StringConstantSlots = new SurtrNativeArray<int>(literalCount);
            for (int i = 0; i < literalCount; i++)
            {
                chunk.StringConstants[i] = state.Text();
                chunk.StringConstantSlots[i] = reader.ReadInt32();
            }

            int typeCount = state.Count();
            chunk.TypeTable = new SurtrTypeHandle[typeCount];
            for (int i = 0; i < typeCount; i++)
                chunk.TypeTable[i] = state.Handle(state.Text());

            int moduleCount = state.Count();
            chunk.PendingModulePaths = new string[moduleCount];
            for (int i = 0; i < moduleCount; i++)
                chunk.PendingModulePaths[i] = state.Text();

            chunk.ModuleTable = moduleCount == 0 ? Array.Empty<SurtrModule>() : new SurtrModule[moduleCount];

            int fieldCount = state.Count();
            chunk.PendingFields = new SurtrPendingMember[fieldCount];
            for (int i = 0; i < fieldCount; i++)
                chunk.PendingFields[i] = ReadMemberReference(state, withSignature: false);

            chunk.FieldTable = fieldCount == 0 ? Array.Empty<SurtrFieldInfo>() : new SurtrFieldInfo[fieldCount];

            int methodCount = state.Count();
            chunk.PendingMethods = new SurtrPendingMember[methodCount];
            for (int i = 0; i < methodCount; i++)
                chunk.PendingMethods[i] = ReadMemberReference(state, withSignature: true);

            chunk.MethodTable = methodCount == 0 ? Array.Empty<SurtrMethodInfo>() : new SurtrMethodInfo[methodCount];
        }

        private static string[] ReadNameList(ReaderState state)
        {
            int count = state.Count();
            if (count == 0)
                return Array.Empty<string>();

            var names = new string[count];
            for (int i = 0; i < count; i++)
                names[i] = state.Text();

            return names;
        }

        private static SurtrPendingMember ReadMemberReference(ReaderState state, bool withSignature)
        {
            byte owner = state.Reader.ReadByte();

            string? ownerDescriptor = owner switch
            {
                SurtrModuleImageWriter.OwnerType => state.Text(),
                SurtrModuleImageWriter.OwnerModule => null,
                _ => throw new SurtrImageFormatException($"The image tags a member reference with owner kind {owner}, which is not one."),
            };

            string name = state.Text();
            string? signatureKey = withSignature ? state.Text() : null;

            return new SurtrPendingMember(ownerDescriptor, name, signatureKey);
        }

        #endregion

        #region Declarations

        private static void ReadModuleDeclarations(ReaderState state)
        {
            var module = state.Module;

            int fieldCount = state.Count();
            for (int i = 0; i < fieldCount; i++)
                module.AddField(ReadField(state, declaringType: null));

            // Properties are read before methods but attached after them, because a property points
            // at accessors that are ordinary methods of the same owner.
            int propertyCount = state.Count();
            var properties = new PendingProperty[propertyCount];
            for (int i = 0; i < propertyCount; i++)
                properties[i] = ReadProperty(state, declaringType: null);

            int methodCount = state.Count();
            var methods = new List<SurtrMethodInfo>(methodCount);
            for (int i = 0; i < methodCount; i++)
            {
                var method = ReadMethod(state, declaringType: null, module.Chunk);
                methods.Add(method);
                module.AddMethod(method);
            }

            for (int i = 0; i < properties.Length; i++)
                module.AddProperty(properties[i].Build(state, methods));

            int classCount = state.Count();
            for (int i = 0; i < classCount; i++)
                module.AddClass(ReadClass(state, module.Path, declaringType: null));

            int interfaceCount = state.Count();
            for (int i = 0; i < interfaceCount; i++)
                module.AddInterface(ReadInterface(state, module.Path, declaringType: null));
        }

        private static SurtrFieldInfo ReadField(ReaderState state, SurtrTypeHandle? declaringType)
        {
            string name = state.Text();
            var fieldType = state.Handle(state.Text());
            bool isStatic = state.Reader.ReadBoolean();
            bool isReadOnly = state.Reader.ReadBoolean();
            var visibility = (SurtrVisibility)state.Reader.ReadByte();

            var field = new SurtrFieldInfo(name, fieldType, isStatic, isReadOnly, visibility, declaringType);
            ReadAttributes(state, field);
            return field;
        }

        private readonly struct PendingProperty
        {
            internal readonly string Name;
            internal readonly SurtrTypeHandle PropertyType;
            internal readonly bool IsStatic;
            internal readonly SurtrVisibility Visibility;
            internal readonly string? GetterKey;
            internal readonly string? SetterKey;
            internal readonly SurtrTypeHandle? DeclaringType;
            internal readonly SurtrAttributeUsage[] Attributes;

            internal PendingProperty(
                string name,
                SurtrTypeHandle propertyType,
                bool isStatic,
                SurtrVisibility visibility,
                string? getterKey,
                string? setterKey,
                SurtrTypeHandle? declaringType,
                SurtrAttributeUsage[] attributes)
            {
                Name = name;
                PropertyType = propertyType;
                IsStatic = isStatic;
                Visibility = visibility;
                GetterKey = getterKey;
                SetterKey = setterKey;
                DeclaringType = declaringType;
                Attributes = attributes;
            }

            internal SurtrPropertyInfo Build(ReaderState state, List<SurtrMethodInfo> candidates)
            {
                var property = new SurtrPropertyInfo(
                    Name,
                    PropertyType,
                    Find(GetterKey, candidates),
                    Find(SetterKey, candidates),
                    IsStatic,
                    Visibility,
                    DeclaringType);

                for (int i = 0; i < Attributes.Length; i++)
                    property.AddAttribute(Attributes[i]);

                return property;
            }

            private SurtrMethodInfo? Find(string? key, List<SurtrMethodInfo> candidates)
            {
                if (key is null)
                    return null;

                for (int i = 0; i < candidates.Count; i++)
                {
                    if (string.Equals(candidates[i].SignatureKey(), key, StringComparison.Ordinal))
                        return candidates[i];
                }

                throw new SurtrImageFormatException(
                    $"Property '{Name}' names an accessor '{key}' that its declaring type does not declare.");
            }
        }

        private static PendingProperty ReadProperty(ReaderState state, SurtrTypeHandle? declaringType)
        {
            string name = state.Text();
            var propertyType = state.Handle(state.Text());
            bool isStatic = state.Reader.ReadBoolean();
            var visibility = (SurtrVisibility)state.Reader.ReadByte();

            string? getter = state.Reader.ReadBoolean() ? state.Text() : null;
            string? setter = state.Reader.ReadBoolean() ? state.Text() : null;

            return new PendingProperty(
                name, propertyType, isStatic, visibility, getter, setter, declaringType, ReadAttributeList(state));
        }

        private static SurtrMethodInfo ReadMethod(ReaderState state, SurtrTypeHandle? declaringType, SurtrChunk chunk)
        {
            var reader = state.Reader;

            string name = state.Text();
            var returnType = state.Handle(state.Text());
            var implKind = (SurtrMethodImplKind)reader.ReadByte();
            var dispatch = (SurtrMethodDispatch)reader.ReadByte();
            var role = (SurtrMethodRole)reader.ReadByte();
            var visibility = (SurtrVisibility)reader.ReadByte();
            bool isStatic = reader.ReadBoolean();
            bool isOverride = reader.ReadBoolean();
            bool isSealed = reader.ReadBoolean();
            bool isExtension = reader.ReadBoolean();

            int parameterCount = state.Count();
            var parameters = parameterCount == 0
                ? Array.Empty<SurtrParameterInfo>()
                : new SurtrParameterInfo[parameterCount];

            for (int i = 0; i < parameterCount; i++)
                parameters[i] = ReadParameter(state);

            // The method's own generic parameters, names then per-parameter constraint lists -
            // read before the impl kind branches because every kind can carry them.
            int genericCount = state.Count();
            string[]? genericParameters = null;
            string[][]? genericConstraints = null;

            if (genericCount > 0)
            {
                genericParameters = new string[genericCount];
                for (int i = 0; i < genericCount; i++)
                    genericParameters[i] = state.Text();

                genericConstraints = new string[genericCount][];
                for (int i = 0; i < genericCount; i++)
                {
                    int boundCount = state.Count();
                    var bounds = boundCount == 0
                        ? Array.Empty<string>()
                        : new string[boundCount];

                    for (int b = 0; b < boundCount; b++)
                        bounds[b] = state.Text();

                    genericConstraints[i] = bounds;
                }
            }

            if (implKind == SurtrMethodImplKind.Abstract)
            {
                var contractMethod = new SurtrAbstractMethodInfo(
                    name, returnType, parameters, visibility, declaringType,
                    genericParameters, genericConstraints, isExtension);
                ReadAttributes(state, contractMethod);
                return contractMethod;
            }

            if (implKind == SurtrMethodImplKind.Native)
            {
                // Unbound on purpose: the address comes from whichever runtime loads the module,
                // through the link name that was just read.
                var nativeMethod = new SurtrNativeMethodInfo(
                    name, dispatch, role, isOverride, returnType, parameters,
                    isStatic, visibility, declaringType, state.Text(), isSealed,
                    genericParameters, genericConstraints, isExtension);

                ReadAttributes(state, nativeMethod);
                return nativeMethod;
            }

            if (implKind != SurtrMethodImplKind.Bytecode)
                throw new SurtrImageFormatException($"The image holds a method with impl kind {implKind}, which an image cannot carry.");

            int entryIndex = reader.ReadInt32();
            int localCount = reader.ReadInt32();
            int maxStackSize = reader.ReadInt32();

            if ((uint)entryIndex >= (uint)chunk.MethodOffsets.Length)
                throw new SurtrImageFormatException($"Method '{name}' names entry {entryIndex}, which the chunk's offset table does not hold.");

            var method = new SurtrBytecodeMethodInfo(
                name, dispatch, role, isOverride, returnType, parameters,
                isStatic, visibility, declaringType,
                chunk, entryIndex, localCount, maxStackSize, isSealed,
                genericParameters, genericConstraints, isExtension);

            int handlerCount = state.Count();
            if (handlerCount != 0)
            {
                var handlers = new SurtrExceptionHandler[handlerCount];
                for (int i = 0; i < handlerCount; i++)
                {
                    int tryStart = reader.ReadInt32();
                    int tryEnd = reader.ReadInt32();
                    int handlerOffset = reader.ReadInt32();
                    string? catchType = state.OptionalText();

                    handlers[i] = new SurtrExceptionHandler(
                        tryStart, tryEnd, handlerOffset, catchType is null ? null : state.Handle(catchType));
                }

                method.SetExceptionHandlers(handlers);
            }

            ReadAttributes(state, method);
            return method;
        }

        private static SurtrParameterInfo ReadParameter(ReaderState state)
        {
            string name = state.Text();
            var parameterType = state.Handle(state.Text());
            bool isVarargs = state.Reader.ReadBoolean();
            var defaultValue = ReadConstant(state);

            return isVarargs || defaultValue.HasValue
                ? new SurtrParameterInfo(name, parameterType, defaultValue, isVarargs)
                : new SurtrParameterInfo(name, parameterType);
        }

        private static SurtrConstant ReadConstant(ReaderState state)
        {
            var kind = (SurtrConstantKind)state.Reader.ReadByte();

            switch (kind)
            {
                case SurtrConstantKind.None:
                    return SurtrConstant.None;

                case SurtrConstantKind.Null:
                    return SurtrConstant.Null;

                case SurtrConstantKind.String:
                    return SurtrConstant.String(state.Text());

                case SurtrConstantKind.Integer:
                    return SurtrConstant.Integer(SurtrValueOf(state).AsInt);

                case SurtrConstantKind.Float:
                    return SurtrConstant.Float(SurtrValueOf(state).AsFloat);

                case SurtrConstantKind.Boolean:
                    return SurtrConstant.Boolean(SurtrValueOf(state).AsBool);

                case SurtrConstantKind.Character:
                    return SurtrConstant.Character(SurtrValueOf(state).AsChar);

                default:
                    throw new SurtrImageFormatException($"The image holds a constant of kind {kind}, which is not one.");
            }
        }

        private static Runtime.Objects.SurtrValue SurtrValueOf(ReaderState state)
            => Runtime.Objects.SurtrValue.FromRaw(state.Reader.ReadUInt64());

        private static void ReadAttributes(ReaderState state, SurtrMemberInfo member)
        {
            var attributes = ReadAttributeList(state);
            for (int i = 0; i < attributes.Length; i++)
                member.AddAttribute(attributes[i]);
        }

        private static SurtrAttributeUsage[] ReadAttributeList(ReaderState state)
        {
            int count = state.Count();
            if (count == 0)
                return Array.Empty<SurtrAttributeUsage>();

            var attributes = new SurtrAttributeUsage[count];
            for (int i = 0; i < count; i++)
            {
                var attributeType = state.Handle(state.Text());

                int argumentCount = state.Count();
                var arguments = argumentCount == 0 ? Array.Empty<SurtrConstant>() : new SurtrConstant[argumentCount];
                for (int a = 0; a < argumentCount; a++)
                    arguments[a] = ReadConstant(state);

                attributes[i] = new SurtrAttributeUsage(attributeType, arguments);
            }

            return attributes;
        }

        private static SurtrClass ReadClass(ReaderState state, string ownerPath, SurtrTypeHandle? declaringType)
        {
            var reader = state.Reader;

            string name = state.Text();
            var typeCode = (SurtrValueTypeCode)reader.ReadByte();
            var visibility = (SurtrVisibility)reader.ReadByte();
            bool isAbstract = reader.ReadBoolean();
            bool isSealed = reader.ReadBoolean();
            bool isEnum = reader.ReadBoolean();
            bool isValueType = reader.ReadBoolean();

            string? baseDescriptor = state.OptionalText();

            // Read here because that is where they were written; attached once the type exists.
            var attributes = ReadAttributeList(state);

            // The full name is rebuilt from where the class sits, exactly as SurtrClassBuilder
            // composes it, rather than written: a name and a position determine it, and two
            // spellings of one thing can disagree.
            string separator = declaringType is null
                ? SurtrClassReference.ModuleSeparator.ToString()
                : SurtrClassReference.NameSeparator.ToString();

            string fullName = ownerPath + separator + name;
            var selfReference = SurtrClassReference.Object(fullName);
            var selfHandle = state.Handle(selfReference.Descriptor);

            var type = new SurtrClass(
                name,
                typeCode,
                selfReference,
                baseDescriptor is null ? null : state.Handle(baseDescriptor),
                isAbstract,
                visibility,
                declaringType,
                isSealed && !isEnum,
                isEnum);

            // Before linking: the linker reads it to decide between the ordinary one-slot-per-
            // field layout and the flattened value layout.
            type.IsValueType = isValueType;

            if (!selfHandle.IsResolved)
                selfHandle.Resolve(type);

            for (int i = 0; i < attributes.Length; i++)
                type.AddAttribute(attributes[i]);

            int interfaceCount = state.Count();
            if (interfaceCount != 0)
            {
                var interfaces = new SurtrTypeHandle[interfaceCount];
                for (int i = 0; i < interfaceCount; i++)
                    interfaces[i] = state.Handle(state.Text());

                type.SetDeclaredInterfaces(interfaces);
            }

            int genericCount = state.Count();
            if (genericCount != 0)
            {
                var genericParameters = new string[genericCount];
                for (int i = 0; i < genericCount; i++)
                    genericParameters[i] = state.Text();

                type.SetGenericParameters(genericParameters);

                // One variance byte per parameter, written after the names. An all-invariant table
                // is the same answer an unannotated declaration gives, so it round-trips as data
                // rather than being inferred.
                var genericVariance = new SurtrGenericVariance[genericCount];
                for (int i = 0; i < genericCount; i++)
                    genericVariance[i] = (SurtrGenericVariance)reader.ReadByte();

                type.SetGenericVariance(genericVariance);

                var genericConstraints = new string[genericCount][];
                for (int i = 0; i < genericCount; i++)
                {
                    int boundCount = state.Count();
                    var bounds = new string[boundCount];
                    for (int b = 0; b < boundCount; b++)
                        bounds[b] = state.Text();

                    genericConstraints[i] = bounds;
                }

                type.SetGenericConstraints(genericConstraints);
            }

            int caseCount = state.Count();
            for (int i = 0; i < caseCount; i++)
            {
                string caseName = state.Text();
                int caseValue = reader.ReadInt32();
                var caseVisibility = (SurtrVisibility)reader.ReadByte();

                // Through AddEnumCase so the ordinal is assigned by declaration order here, the
                // same way it was when the enum was first declared. The value travels explicitly:
                // it is the key a switch dispatches on, not something to re-derive from position.
                type.AddEnumCase(new SurtrFieldInfo(caseName, selfHandle, true, true, caseVisibility, selfHandle), caseValue);
            }

            int fieldCount = state.Count();
            for (int i = 0; i < fieldCount; i++)
                type.AddField(ReadField(state, selfHandle));

            int propertyCount = state.Count();
            var properties = new PendingProperty[propertyCount];
            for (int i = 0; i < propertyCount; i++)
                properties[i] = ReadProperty(state, selfHandle);

            int methodCount = state.Count();
            var methods = new List<SurtrMethodInfo>(methodCount);
            for (int i = 0; i < methodCount; i++)
            {
                var method = ReadMethod(state, selfHandle, state.Module.Chunk);
                methods.Add(method);
                type.AddMethod(method);
            }

            for (int i = 0; i < properties.Length; i++)
                type.AddProperty(properties[i].Build(state, methods));

            int nestedClassCount = state.Count();
            for (int i = 0; i < nestedClassCount; i++)
                type.AddNestedClass(ReadClass(state, fullName, selfHandle));

            int nestedInterfaceCount = state.Count();
            for (int i = 0; i < nestedInterfaceCount; i++)
                type.AddNestedInterface(ReadInterface(state, fullName, selfHandle));

            return type;
        }

        private static SurtrInterface ReadInterface(ReaderState state, string ownerPath, SurtrTypeHandle? declaringType)
        {
            string name = state.Text();
            var visibility = (SurtrVisibility)state.Reader.ReadByte();
            var attributes = ReadAttributeList(state);

            string separator = declaringType is null
                ? SurtrClassReference.ModuleSeparator.ToString()
                : SurtrClassReference.NameSeparator.ToString();

            var selfReference = SurtrClassReference.Object(ownerPath + separator + name);
            var selfHandle = state.Handle(selfReference.Descriptor);

            var contract = new SurtrInterface(name, selfReference, visibility, declaringType);

            if (!selfHandle.IsResolved)
                selfHandle.Resolve(contract);

            for (int i = 0; i < attributes.Length; i++)
                contract.AddAttribute(attributes[i]);

            int extendedCount = state.Count();
            if (extendedCount != 0)
            {
                var extended = new SurtrTypeHandle[extendedCount];
                for (int i = 0; i < extendedCount; i++)
                    extended[i] = state.Handle(state.Text());

                contract.SetDeclaredExtendedInterfaces(extended);
            }

            int genericCount = state.Count();
            if (genericCount != 0)
            {
                var genericParameters = new string[genericCount];
                for (int i = 0; i < genericCount; i++)
                    genericParameters[i] = state.Text();

                contract.SetGenericParameters(genericParameters);

                // One variance byte per parameter, written after the names - the interface twin of
                // what ReadClass reads, so both kinds answer variance questions identically.
                var genericVariance = new SurtrGenericVariance[genericCount];
                for (int i = 0; i < genericCount; i++)
                    genericVariance[i] = (SurtrGenericVariance)state.Reader.ReadByte();

                contract.SetGenericVariance(genericVariance);

                var genericConstraints = new string[genericCount][];
                for (int i = 0; i < genericCount; i++)
                {
                    int boundCount = state.Count();
                    var bounds = new string[boundCount];
                    for (int b = 0; b < boundCount; b++)
                        bounds[b] = state.Text();

                    genericConstraints[i] = bounds;
                }

                contract.SetGenericConstraints(genericConstraints);
            }

            int methodCount = state.Count();
            var methods = new List<SurtrMethodInfo>(methodCount);
            for (int i = 0; i < methodCount; i++)
            {
                var method = ReadMethod(state, selfHandle, state.Module.Chunk);
                methods.Add(method);
                contract.AddMethod(method);
            }

            int propertyCount = state.Count();
            for (int i = 0; i < propertyCount; i++)
                contract.AddProperty(ReadProperty(state, selfHandle).Build(state, methods));

            return contract;
        }

        #endregion
    }
}
