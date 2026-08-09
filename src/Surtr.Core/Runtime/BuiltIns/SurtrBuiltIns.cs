#nullable enable

using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using System.Runtime.CompilerServices;

namespace Surtr.Runtime.BuiltIns
{
    /// <summary>
    /// The built-in classes: one shared, process-wide <see cref="SurtrClass"/> per type family,
    /// built once and used by every <see cref="SurtrRuntime"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why they are shared.</b> There is nothing per-runtime about what <c>string</c> means, and
    /// a great deal that goes wrong if two runtimes disagree: a native entry point registered
    /// against one runtime's string class would not match the other's, and a class handed across
    /// would fail its own <see cref="SurtrClass.IsSubclassOf"/> check. Building them once also
    /// keeps runtime construction cheap, which matters when a host spins up a sandbox per script.
    /// </para>
    /// <para>
    /// <b>Why they are not registered with any entity registry.</b> A built-in class is not a
    /// collectable value, and it could not be one even if it wanted to: a
    /// <see cref="SurtrRuntimeEntity"/> holds a single <see cref="SurtrRef"/>, so registering one
    /// shared class in two registries would have the second silently inherit the first's id.
    /// Metadata is instead owned outright - by this type for the built-ins, by the context for
    /// everything else - and lives as long as its owner. Tracing skips it entirely, which is why
    /// <see cref="SurtrObject.VisitReferences"/> does not mark an object's class.
    /// </para>
    /// <para>
    /// <b>Why one class covers every parameterisation.</b> <c>int[]</c> and <c>string[]</c> share
    /// <see cref="Array"/>, and <c>{int: string}</c> and <c>{string: float}</c> share
    /// <see cref="Dictionary"/>. Surtr is statically typed with no dynamic top type, so an
    /// array-typed expression's element type is settled at compile time and no runtime check ever
    /// has to ask - which makes a class per parameterisation pure cost. Their
    /// <see cref="SurtrTypeInfo.SelfReference"/> is correspondingly the bare family symbol
    /// (<c>A</c>, <c>D</c>, <c>T</c>, <c>L</c>), deliberately not a well-formed descriptor: it
    /// names the family and says nothing about parameters, because that is exactly what the class
    /// knows. The parameterised descriptor lives on each object instead, in
    /// <see cref="SurtrArray.TypeReference"/> and its siblings.
    /// </para>
    /// <para>
    /// <b>There is no root class.</b> Every built-in sits at depth 0 in its own hierarchy; nothing
    /// derives from anything. A common <c>object</c> root would only earn its keep if values of
    /// unrelated types had to be held in one place, which a language with no top type never
    /// requires.
    /// </para>
    /// </remarks>
    public static class SurtrBuiltIns
    {
        /// <summary>The module path the built-in classes report as their home.</summary>
        public const string ModulePath = "surtr";

        /// <summary>The full name in <see cref="NativeObject"/>'s descriptor.</summary>
        public const string NativeObjectFullName = "surtr:native";

        /// <summary>The module owning every built-in class, its type handles, and their linkage.</summary>
        public static readonly SurtrModule Module;

        /// <summary>The <c>int</c> class, shared by raw and boxed integers alike.</summary>
        public static readonly SurtrClass Integer;

        /// <summary>The <c>float</c> class.</summary>
        public static readonly SurtrClass Float;

        /// <summary>The <c>bool</c> class.</summary>
        public static readonly SurtrClass Boolean;

        /// <summary>The <c>char</c> class.</summary>
        public static readonly SurtrClass Character;

        /// <summary>The <c>string</c> class, behind every <see cref="SurtrString"/>.</summary>
        public static readonly SurtrClass String;

        /// <summary>The <c>array</c> class, behind every <see cref="SurtrArray"/> whatever its element type.</summary>
        public static readonly SurtrClass Array;

        /// <summary>The <c>tuple</c> class, behind every <see cref="SurtrTuple"/> whatever its shape.</summary>
        public static readonly SurtrClass Tuple;

        /// <summary>The <c>dict</c> class, behind every <see cref="SurtrDictionary"/> whatever its key and value types.</summary>
        public static readonly SurtrClass Dictionary;

        /// <summary>The <c>closure</c> class, behind every <see cref="SurtrClosure"/> whatever its signature.</summary>
        public static readonly SurtrClass Closure;

        /// <summary>The <c>range</c> class, behind every <see cref="SurtrRange"/>.</summary>
        public static readonly SurtrClass Range;

        /// <summary>
        /// The root native class, behind a <see cref="SurtrNativeProxy"/> the host did not declare
        /// a type for. Host-declared native classes are separate and live on the runtime.
        /// </summary>
        public static readonly SurtrClass NativeObject;

        /// <summary>
        /// The class an erased generic type parameter resolves to.
        /// </summary>
        /// <remarks>
        /// Abstract and memberless, like <see cref="Void"/>, and for the same reason: nothing is
        /// ever an instance of it. It exists so a field or parameter declared as <c>T</c> has a
        /// handle that resolves like any other, which is what lets a generic class be linked
        /// without knowing a single type argument. Its <see cref="SurtrValueTypeCode"/> is a
        /// reference type, so an erased slot is traced by the collector and a primitive has to be
        /// boxed on the way in - the same bargain erasure strikes on the JVM.
        /// </remarks>
        public static readonly SurtrClass Erased;

        /// <summary>
        /// The marker class for <see cref="SurtrValueTypeCode.Void"/>.
        /// </summary>
        /// <remarks>
        /// Abstract, memberless, and never instantiated - the same role <c>System.Void</c> plays in
        /// the CLR. It exists so a method that returns nothing still has a return handle that
        /// resolves like any other, rather than being the one type reference in the system that
        /// stays permanently unbound.
        /// </remarks>
        public static readonly SurtrClass Void;

        /// <summary>Every built-in class indexed by its <see cref="SurtrValueTypeCode"/>, so the lookup is one load.</summary>
        private static readonly SurtrClass[] ByTypeCode;

        static SurtrBuiltIns()
        {
            Module = new SurtrModule(ModulePath);
            var handles = Module.TypeHandles;

            // The composites use the bare family symbol as their descriptor. It is intentionally
            // not a well-formed descriptor - see the type remarks - and it still reads back the
            // right SurtrValueTypeCode, which is the only thing anything asks it for.
            Integer = Declare("int", SurtrValueTypeCode.Integer, SurtrClassReference.Integer);
            Float = Declare("float", SurtrValueTypeCode.Float, SurtrClassReference.Float);
            Boolean = Declare("bool", SurtrValueTypeCode.Boolean, SurtrClassReference.Boolean);
            Character = Declare("char", SurtrValueTypeCode.Character, SurtrClassReference.Character);
            String = Declare("string", SurtrValueTypeCode.String, SurtrClassReference.String);
            Array = Declare("array", SurtrValueTypeCode.Array, SurtrClassReference.FromDescriptor(SurtrClassReference.SymbolArray.ToString()));
            Tuple = Declare("tuple", SurtrValueTypeCode.Tuple, SurtrClassReference.FromDescriptor(SurtrClassReference.SymbolTuple.ToString()));
            Dictionary = Declare("dict", SurtrValueTypeCode.Dictionary, SurtrClassReference.FromDescriptor(SurtrClassReference.SymbolDictionary.ToString()));
            Closure = Declare("closure", SurtrValueTypeCode.Closure, SurtrClassReference.FromDescriptor(SurtrClassReference.SymbolClosure.ToString()));
            // Unlike its neighbours this one's SelfReference is a well-formed descriptor, because
            // a range has nothing to be parameterised by: R names the type completely.
            Range = Declare("range", SurtrValueTypeCode.Range, SurtrClassReference.Range);
            NativeObject = Declare("native", SurtrValueTypeCode.Native, SurtrClassReference.Native(NativeObjectFullName));
            Erased = Declare("erased", SurtrValueTypeCode.Erased, SurtrClassReference.Erased, isAbstract: true);
            Void = Declare("void", SurtrValueTypeCode.Void, SurtrClassReference.Void, isAbstract: true);

            // Built before anything can ask for it: the handle-binding pass below goes through
            // ForTypeCode, which reads this table.
            ByTypeCode = new SurtrClass[(int)SurtrValueTypeCode.Void + 1];
            ByTypeCode[(int)SurtrValueTypeCode.Integer] = Integer;
            ByTypeCode[(int)SurtrValueTypeCode.Float] = Float;
            ByTypeCode[(int)SurtrValueTypeCode.Boolean] = Boolean;
            ByTypeCode[(int)SurtrValueTypeCode.Character] = Character;
            ByTypeCode[(int)SurtrValueTypeCode.String] = String;
            ByTypeCode[(int)SurtrValueTypeCode.Array] = Array;
            ByTypeCode[(int)SurtrValueTypeCode.Tuple] = Tuple;
            ByTypeCode[(int)SurtrValueTypeCode.Dictionary] = Dictionary;
            ByTypeCode[(int)SurtrValueTypeCode.Closure] = Closure;
            ByTypeCode[(int)SurtrValueTypeCode.Range] = Range;
            ByTypeCode[(int)SurtrValueTypeCode.Native] = NativeObject;
            ByTypeCode[(int)SurtrValueTypeCode.Erased] = Erased;
            ByTypeCode[(int)SurtrValueTypeCode.Void] = Void;

            // The element-polymorphic members are declared against these: array.push takes G0,
            // dict.get maps G0 to G1. Tuple and closure declare none on purpose - both are
            // parameterised by a *list* whose length varies per value, so a fixed parameter list
            // could not describe either, and neither has a member that would want one.
            Array.SetGenericParameters("T");
            Dictionary.SetGenericParameters("K", "V");

            SurtrPrimitiveBuiltIns.DeclareInteger(BuilderFor(Integer, handles));
            SurtrPrimitiveBuiltIns.DeclareFloat(BuilderFor(Float, handles));
            SurtrPrimitiveBuiltIns.DeclareBoolean(BuilderFor(Boolean, handles));
            SurtrPrimitiveBuiltIns.DeclareCharacter(BuilderFor(Character, handles));
            SurtrStringBuiltIn.Declare(BuilderFor(String, handles));
            SurtrCompositeBuiltIns.DeclareArray(BuilderFor(Array, handles));
            SurtrCompositeBuiltIns.DeclareTuple(BuilderFor(Tuple, handles));
            SurtrCompositeBuiltIns.DeclareDictionary(BuilderFor(Dictionary, handles));
            SurtrCompositeBuiltIns.DeclareClosure(BuilderFor(Closure, handles));
            SurtrCompositeBuiltIns.DeclareRange(BuilderFor(Range, handles));

            // Every handle the built-in signatures mention is a built-in, so the whole table binds
            // from what was just declared - the built-in module is the one module with no external
            // dependencies, which is what lets it link before any runtime exists.
            foreach (var handle in handles.Handles)
            {
                if (!handle.IsResolved)
                    handle.Resolve(ForTypeCode(handle.Reference.TypeCode));
            }

            SurtrTypeLinker.LinkModule(Module);
            Module.MarkLoaded();
        }

        private static SurtrClass Declare(string name, SurtrValueTypeCode typeCode, SurtrClassReference selfReference, bool isAbstract = false)
        {
            var declared = new SurtrClass(
                name,
                typeCode,
                selfReference,
                baseType: null,
                isAbstract,
                SurtrVisibility.Public,
                declaringType: null);

            Module.AddClass(declared);
            return declared;
        }

        private static SurtrBuiltInTypeBuilder BuilderFor(SurtrClass declared, SurtrTypeHandleTable handles)
        {
            var selfHandle = handles.GetOrAdd(declared.SelfReference);
            if (!selfHandle.IsResolved)
                selfHandle.Resolve(declared);

            return new SurtrBuiltInTypeBuilder(declared, selfHandle, handles);
        }

        /// <summary>
        /// Forces the one-time construction of the built-in classes.
        /// </summary>
        /// <remarks>
        /// A static constructor already guarantees this happens exactly once and before any member
        /// is read; this only lets a runtime choose <em>when</em>, so the cost lands during its own
        /// construction rather than inside whichever frame first allocates a string.
        /// </remarks>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void EnsureBuilt()
        {
            // Reading any static member is enough to run the static constructor.
            _ = Module;
        }

        /// <summary>
        /// The built-in class for a type family.
        /// </summary>
        /// <remarks>
        /// This is where every parameterisation collapses: pass the code off <c>AI</c>, <c>AS</c>
        /// or <c>ADIS</c> and all three answer <see cref="Array"/>.
        /// </remarks>
        /// <exception cref="System.ArgumentOutOfRangeException"><paramref name="typeCode"/> does not name a real family.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SurtrClass ForTypeCode(SurtrValueTypeCode typeCode)
        {
            var table = ByTypeCode;
            int index = (int)typeCode;

            if ((uint)index >= (uint)table.Length || table[index] is null)
                throw new System.ArgumentOutOfRangeException(nameof(typeCode), typeCode, "No built-in class corresponds to that type code.");

            return table[index];
        }

        /// <summary>
        /// The class a NaN-boxed primitive belongs to.
        /// </summary>
        /// <remarks>
        /// Reads the tag rather than consulting anything, which is what makes an unboxed primitive
        /// able to answer "what class am I" as cheaply as a boxed one - the two are the same value
        /// and must give the same answer.
        /// </remarks>
        /// <exception cref="System.ArgumentException"><paramref name="value"/> is a reference, whose class comes from the object it names.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SurtrClass ForValue(SurtrValue value)
        {
            if (value.IsInt) return Integer;
            if (value.IsFloat) return Float;
            if (value.IsBool) return Boolean;
            if (value.IsChar) return Character;

            throw new System.ArgumentException("A reference's class comes from the object it names, not from its tag.", nameof(value));
        }
    }
}
