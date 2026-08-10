#nullable enable

using Surtr.Runtime.Classes;

namespace Surtr.Tests.VM
{
    /// <summary>
    /// Small metadata-construction helpers shared by the VM opcode suites - building a linked
    /// class or a field by hand is common to the object/field/dispatch/exception-type tests, and
    /// there is no compiler yet to do it for them.
    /// </summary>
    internal static class VmMetadataHelpers
    {
        public static SurtrTypeHandle HandleFor(SurtrModule module, SurtrClassReference reference)
            => module.TypeHandles.GetOrAdd(reference);

        public static SurtrTypeHandle HandleFor(SurtrModule module, SurtrTypeInfo type)
        {
            var handle = module.TypeHandles.GetOrAdd(type.SelfReference);
            if (!handle.IsResolved)
                handle.Resolve(type);
            return handle;
        }

        public static SurtrClass DefineClass(
            SurtrModule module,
            string name,
            SurtrClass? baseClass = null,
            bool isAbstract = false,
            SurtrInterface[]? interfaces = null)
        {
            var selfReference = SurtrClassReference.Object($"test:{name}");
            var baseHandle = baseClass is null ? null : HandleFor(module, baseClass);

            var type = new SurtrClass(name, SurtrValueTypeCode.Object, selfReference, baseHandle, isAbstract, SurtrVisibility.Public, declaringType: null);

            if (interfaces is { Length: > 0 })
            {
                var handles = new SurtrTypeHandle[interfaces.Length];
                for (int i = 0; i < interfaces.Length; i++)
                    handles[i] = HandleFor(module, interfaces[i]);
                type.SetDeclaredInterfaces(handles);
            }

            module.AddClass(type);
            return type;
        }

        public static SurtrFieldInfo Field(SurtrModule module, string name, SurtrClassReference type, bool isStatic = false)
            => new(name, HandleFor(module, type), isStatic, isReadOnly: false, SurtrVisibility.Public, declaringType: null);
    }
}
