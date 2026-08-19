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
        /// The <c>iterator</c> class, behind every <see cref="SurtrIterator"/>: the cursor each
        /// built-in collection hands back from <c>iterate()</c>.
        /// </summary>
        /// <remarks>
        /// A library class rather than a value family, so it needs no <see cref="SurtrValueTypeCode"/>
        /// of its own - what it is, is one concrete type satisfying <c>IIterator&lt;T&gt;</c>, the
        /// same shape <see cref="Exception"/> has.
        /// </remarks>
        public static readonly SurtrClass Iterator;

        /// <summary>
        /// <c>IIterator&lt;T&gt;</c>: the two-member cursor <c>for-in</c> is defined against (§4.2).
        /// </summary>
        public static readonly SurtrInterface IIterator;

        /// <summary>
        /// <c>IIterable&lt;T&gt;</c>: what a <c>for-in</c> over anything but a built-in collection
        /// calls <c>iterate()</c> on.
        /// </summary>
        public static readonly SurtrInterface IIterable;

        /// <summary><c>IComparable&lt;T&gt;</c>, which <c>&lt;=&gt;</c> lowers to for a user type.</summary>
        public static readonly SurtrInterface IComparable;

        /// <summary><c>IEquatable&lt;T&gt;</c>.</summary>
        public static readonly SurtrInterface IEquatable;

        /// <summary>
        /// The root of everything throwable. Carries a message, and is extendable from Surtr source.
        /// </summary>
        /// <remarks>
        /// Every thrown value's type must extend this, which is what lets a <c>catch</c> clause
        /// match against a real hierarchy rather than an arbitrary object.
        /// </remarks>
        public static readonly SurtrClass Exception;

        /// <summary>Raised when an argument is outside what a member accepts.</summary>
        public static readonly SurtrClass ArgumentException;

        /// <summary>
        /// Raised when text handed to a parsing constructor (<c>int(aString)</c>,
        /// <c>float(aString)</c>, <c>bool(aString)</c>, <c>char(aString)</c>) is not in the shape
        /// that type expects. Kept distinct from <see cref="ArgumentException"/>: the argument here
        /// is the right <em>type</em> (a string), so the problem is its content, not its shape.
        /// </summary>
        public static readonly SurtrClass FormatException;

        /// <summary>Raised by an array, string or tuple index outside its bounds.</summary>
        public static readonly SurtrClass IndexOutOfRangeException;

        /// <summary>Raised by a dictionary lookup with no entry under that key.</summary>
        public static readonly SurtrClass KeyNotFoundException;

        /// <summary>Raised by a member access on a null receiver.</summary>
        public static readonly SurtrClass NullReferenceException;

        /// <summary>Raised by integer division or remainder by zero.</summary>
        public static readonly SurtrClass DivideByZeroException;

        /// <summary>Raised by a cast that the value's actual class does not satisfy.</summary>
        public static readonly SurtrClass InvalidCastException;

        /// <summary>Raised when the interpreter's own call or value stack is exhausted.</summary>
        public static readonly SurtrClass StackOverflowException;

        /// <summary>Raised by an operation the runtime cannot perform in its current state.</summary>
        public static readonly SurtrClass InvalidOperationException;

        /// <summary>
        /// The root every attribute class extends.
        /// </summary>
        /// <remarks>
        /// Abstract and stateless: what makes an attribute an attribute is deriving from this, and
        /// what it carries is whatever fields its own declaration adds. Requiring a common root is
        /// what lets a use site be checked - <c>@Range(0, 100)</c> has to name a class, and that
        /// class has to be one of these.
        /// </remarks>
        public static readonly SurtrClass Attribute;

        /// <summary>
        /// A first-class handle to another class's metadata, behind every <see cref="SurtrTypeValue"/>.
        /// </summary>
        /// <remarks>
        /// Declares no constructor and no field of its own - the same reason <see cref="Iterator"/>
        /// declares none - so Surtr source can never write <c>Type()</c> itself; the only way to
        /// get one is <c>Type.of(...)</c> or another <c>Type</c>/<c>Member</c> member handing one
        /// back.
        /// </remarks>
        public static readonly SurtrClass Type;

        /// <summary>
        /// A first-class handle to one field, property, method or nested type declaration, behind
        /// every <see cref="SurtrMemberValue"/>.
        /// </summary>
        public static readonly SurtrClass Member;

        /// <summary>
        /// A first-class handle to a <see cref="Classes.SurtrModule"/>, behind every
        /// <see cref="SurtrModuleValue"/> - what <c>moduleof</c> and <c>Module.get</c> return.
        /// </summary>
        /// <remarks>
        /// Named <c>ModuleType</c> rather than <c>Module</c> on the C# side only: <see cref="Module"/>
        /// already names the <see cref="Classes.SurtrModule"/> that owns every built-in class
        /// itself, a completely different thing. The Surtr-visible name is still <c>Module</c>,
        /// same as <see cref="Type"/> and <see cref="Member"/> are named on their own side.
        /// Declares no constructor and no field of its own, so Surtr source can never write
        /// <c>Module()</c> - the only way to get one is <c>moduleof(...)</c>, <c>Module.get</c>/
        /// <c>Module.tryGet</c>, or another <c>Module</c> member handing one back.
        /// </remarks>
        public static readonly SurtrClass ModuleType;

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

        /// <summary>
        /// How many interface ids the built-in module has already claimed, counting up from zero.
        /// </summary>
        /// <remarks>
        /// A runtime's own numbering has to start here rather than at zero. Ids are dense and are
        /// what a class's dispatch table is keyed on, so a module whose first interface took id 0
        /// would collide with <c>IIterator</c> on any class implementing both - and the collision
        /// would not show up as an error, it would show up as a call landing in the wrong method.
        /// The built-in module is linked once, before any runtime exists, which is what makes a
        /// fixed reservation the right shape here.
        /// </remarks>
        public static int ReservedInterfaceIds { get; }

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
            // The library classes. Ordinary Surtr objects with real instance layouts, unlike the
            // value-representation classes above: Surtr source extends Exception, so its state has
            // to be a slot the linker lays out and the collector traces.
            Exception = DeclareObject("Exception");
            ArgumentException = DeclareObject("ArgumentException", Exception);
            FormatException = DeclareObject("FormatException", Exception);
            IndexOutOfRangeException = DeclareObject("IndexOutOfRangeException", Exception);
            KeyNotFoundException = DeclareObject("KeyNotFoundException", Exception);
            NullReferenceException = DeclareObject("NullReferenceException", Exception);
            DivideByZeroException = DeclareObject("DivideByZeroException", Exception);
            InvalidCastException = DeclareObject("InvalidCastException", Exception);
            StackOverflowException = DeclareObject("StackOverflowException", Exception);
            InvalidOperationException = DeclareObject("InvalidOperationException", Exception);
            Attribute = DeclareObject("Attribute", isAbstract: true);
            Type = DeclareObject("Type");
            Member = DeclareObject("Member");
            ModuleType = DeclareObject("Module");
            Iterator = DeclareObject("iterator");

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

            SurtrStandardLibrary.DeclareException(BuilderFor(Exception, handles));
            SurtrStandardLibrary.DeclareExceptionSubclass(BuilderFor(ArgumentException, handles));
            SurtrStandardLibrary.DeclareExceptionSubclass(BuilderFor(FormatException, handles));
            SurtrStandardLibrary.DeclareExceptionSubclass(BuilderFor(IndexOutOfRangeException, handles));
            SurtrStandardLibrary.DeclareExceptionSubclass(BuilderFor(KeyNotFoundException, handles));
            SurtrStandardLibrary.DeclareExceptionSubclass(BuilderFor(NullReferenceException, handles));
            SurtrStandardLibrary.DeclareExceptionSubclass(BuilderFor(DivideByZeroException, handles));
            SurtrStandardLibrary.DeclareExceptionSubclass(BuilderFor(InvalidCastException, handles));
            SurtrStandardLibrary.DeclareExceptionSubclass(BuilderFor(StackOverflowException, handles));
            SurtrStandardLibrary.DeclareExceptionSubclass(BuilderFor(InvalidOperationException, handles));
            SurtrStandardLibrary.DeclareCoreInterfaces(Module, handles);

            // After Attribute, since Type.attributes()/Member.attributes() both name it, and
            // after each other, since Type.members() names Member and Member.declaringType names
            // Type - all three classes already exist by this point, only their members are added
            // here.
            SurtrReflectionBuiltIns.DeclareType(BuilderFor(Type, handles));
            SurtrReflectionBuiltIns.DeclareMember(BuilderFor(Member, handles));
            SurtrModuleReflectionBuiltIns.DeclareModule(BuilderFor(ModuleType, handles));

            // Kept as fields because a compiler has to name them to lower `for-in` and `<=>`: those
            // lowerings are calls through a contract's own slots, and looking one up by a mangled
            // string at every emit site would put the arity convention in two places.
            IIterator = Contract("IIterator", 1);
            IIterable = Contract("IIterable", 1);
            IComparable = Contract("IComparable", 1);
            IEquatable = Contract("IEquatable", 1);

            // After the interfaces exist, because these declare that they satisfy them - the
            // handles would intern either way, but reading the declarations in dependency order is
            // what makes this constructor followable.
            SurtrIteratorBuiltIns.DeclareIterator(BuilderFor(Iterator, handles));
            SurtrIteratorBuiltIns.DeclareIterable(BuilderFor(Array, handles), SurtrIteratorKind.Array);
            SurtrIteratorBuiltIns.DeclareIterable(BuilderFor(String, handles), SurtrIteratorKind.String);
            SurtrIteratorBuiltIns.DeclareIterable(BuilderFor(Tuple, handles), SurtrIteratorKind.Tuple);
            SurtrIteratorBuiltIns.DeclareIterable(BuilderFor(Dictionary, handles), SurtrIteratorKind.Dictionary);
            SurtrIteratorBuiltIns.DeclareIterable(BuilderFor(Range, handles), SurtrIteratorKind.Range);

            // The comparability contracts, declared the same way and for the same reason: a
            // `max<T : IComparable<T>>` names a contract, so the value families have to be able to
            // say they satisfy it. Tuple and closure declare neither contract, deliberately - each
            // is parameterised by a *list* whose length varies per value, so no fixed argument
            // could name the type in `IEquatable<T>`, and their equality is identity either way.
            SurtrContractBuiltIns.DeclareIntegerContracts(BuilderFor(Integer, handles));
            SurtrContractBuiltIns.DeclareFloatContracts(BuilderFor(Float, handles));
            SurtrContractBuiltIns.DeclareBooleanContracts(BuilderFor(Boolean, handles));
            SurtrContractBuiltIns.DeclareCharacterContracts(BuilderFor(Character, handles));
            SurtrContractBuiltIns.DeclareStringContracts(BuilderFor(String, handles));
            SurtrContractBuiltIns.DeclareArrayContracts(BuilderFor(Array, handles));
            SurtrContractBuiltIns.DeclareDictionaryContracts(BuilderFor(Dictionary, handles));
            SurtrContractBuiltIns.DeclareRangeContracts(BuilderFor(Range, handles));

            // Every handle the built-in signatures mention is a built-in, so the whole table binds
            // from what was just declared - the built-in module is the one module with no external
            // dependencies, which is what lets it link before any runtime exists.
            foreach (var handle in handles.Handles)
            {
                if (handle.IsResolved)
                    continue;

                // A library class names itself with an O descriptor, which no type code can answer
                // for - those all collapse onto one shared family class. Everything else in this
                // module is a family, and resolves from the code alone.
                var reference = handle.Reference;
                if (reference.TypeCode.IsObject && reference.TryGetFullName(out string fullName))
                {
                    int separator = fullName.IndexOf(SurtrClassReference.ModuleSeparator);
                    string typeName = separator < 0 ? fullName : fullName.Substring(separator + 1);

                    if (Module.TryGetClass(typeName, out var libraryClass))
                    {
                        handle.Resolve(libraryClass);
                        continue;
                    }

                    if (Module.TryGetInterface(typeName, out var contract))
                    {
                        handle.Resolve(contract);
                        continue;
                    }
                }

                handle.Resolve(ForTypeCode(reference.TypeCode));
            }

            int nextInterfaceId = 0;
            SurtrTypeLinker.LinkModule(Module, ref nextInterfaceId);
            ReservedInterfaceIds = nextInterfaceId;

            Module.MarkLoaded();
        }

        /// <summary>The contract just declared under <paramref name="name"/> and its arity.</summary>
        private static SurtrInterface Contract(string name, int arity)
            => Module.TryGetInterface(SurtrClassReference.MangleArity(name, arity), out var contract)
                ? contract
                // Qualified: this class declares a field of that name for the Surtr exception class.
                : throw new System.InvalidOperationException($"The built-in module declares no '{name}' of arity {arity}.");

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

        /// <summary>
        /// Declares a library class: an ordinary Surtr object type in the built-in module, named
        /// the way any other Surtr class is.
        /// </summary>
        /// <remarks>
        /// Its <c>SelfReference</c> is a well-formed <c>O</c> descriptor, unlike the family classes
        /// above, whose bare symbols name a family and say nothing about parameterisation. These
        /// have no parameterisation to say nothing about: <c>Exception</c> is one concrete type.
        /// </remarks>
        private static SurtrClass DeclareObject(string name, SurtrClass? baseClass = null, bool isAbstract = false)
        {
            var handles = Module.TypeHandles;
            SurtrTypeHandle? baseHandle = null;

            if (baseClass is not null)
            {
                baseHandle = handles.GetOrAdd(baseClass.SelfReference);
                if (!baseHandle.IsResolved)
                    baseHandle.Resolve(baseClass);
            }

            var declared = new SurtrClass(
                name,
                SurtrValueTypeCode.Object,
                SurtrClassReference.Object(ModulePath + SurtrClassReference.ModuleSeparator + name),
                baseHandle,
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
        /// The library class a CLR exception should surface as inside Surtr code, or
        /// <see langword="null"/> if it has no counterpart.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A VM trap answers first, because it names its own class - the pairing between a trap
        /// condition and the class it presents as belongs next to the condition, not here. What is
        /// left is host code: a handful of CLR exception types have obvious counterparts and are
        /// worth mapping so a Surtr <c>catch</c> can name them, and everything else deliberately
        /// does not map, staying a native proxy rather than being forced into a class it is not.
        /// </para>
        /// <para>
        /// Cold: only ever reached once an exception exists.
        /// </para>
        /// </remarks>
        public static SurtrClass? ExceptionClassFor(System.Exception clrException)
        {
            if (clrException is Surtr.VM.SurtrExecutionException trap)
                return trap.SurtrType;

            return clrException switch
            {
                // ArgumentOutOfRange first: it derives from ArgumentException, and an index is what
                // the built-in collections raise it for.
                System.ArgumentOutOfRangeException => IndexOutOfRangeException,
                System.IndexOutOfRangeException => IndexOutOfRangeException,
                System.Collections.Generic.KeyNotFoundException => KeyNotFoundException,
                System.NullReferenceException => NullReferenceException,
                System.DivideByZeroException => DivideByZeroException,
                System.InvalidCastException => InvalidCastException,
                System.FormatException => FormatException,
                System.ArgumentException => ArgumentException,
                System.InvalidOperationException => InvalidOperationException,
                _ => null,
            };
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
