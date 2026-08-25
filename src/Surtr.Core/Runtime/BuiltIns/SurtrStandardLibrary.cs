#nullable enable

using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using System;

namespace Surtr.Runtime.BuiltIns
{
    /// <summary>
    /// The part of the <c>surtr</c> module that is a library rather than a value representation:
    /// the exception hierarchy and the core interfaces.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It lives in the same module the primitives and collections do, extended rather than joined
    /// by a second one, so <c>string</c> and <c>Exception</c> are siblings rather than residents of
    /// different worlds. Everything here is implicitly in scope in every file.
    /// </para>
    /// <para>
    /// All of it is written in C# for now. The dividing line the design calls for is "native if it
    /// needs <c>unsafe</c>, a raw pointer or a VM service; Surtr otherwise", which would put the
    /// exception subclasses and most of this on the Surtr side - but nothing turns Surtr source
    /// into a module yet, and the runtime cannot wait for the compiler to have an
    /// <c>Exception</c> to throw. Moving the expressible half across is a later, mechanical change:
    /// the classes keep their names, their layout and their descriptors.
    /// </para>
    /// </remarks>
    internal static unsafe class SurtrStandardLibrary
    {
        /// <summary>The slot <c>Exception</c> keeps its message in, and every subclass inherits.</summary>
        /// <remarks>
        /// Zero because <c>Exception</c> is the root of the hierarchy and declares the only field in
        /// it. Inherited slots keep their base-class index, so a subclass reads the message from the
        /// same slot no matter how deep it sits - which is what lets the raise helpers here write it
        /// without knowing which exception class they are building.
        /// </remarks>
        internal const int MessageSlot = 0;

        #region Exceptions
        /// <summary>Declares <c>Exception</c> itself: one message, and the property that reads it.</summary>
        internal static void DeclareException(SurtrBuiltInTypeBuilder builder)
        {
            builder.Field("_message", SurtrClassReference.String);

            builder.Property("message", SurtrClassReference.String, SurtrNativeEntryPoint.FromFunctionPointer(&ExceptionMessage));
            builder.Method("toString", SurtrClassReference.String, SurtrNativeEntryPoint.FromFunctionPointer(&ExceptionToString));

            builder.Constructor(
                SurtrNativeEntryPoint.FromFunctionPointer(&ExceptionConstruct),
                builder.Params(("message", SurtrClassReference.String)));
        }

        /// <summary>
        /// Declares a subclass of <c>Exception</c>, which is a constructor and nothing else.
        /// </summary>
        /// <remarks>
        /// The subclasses carry no state of their own: what distinguishes
        /// <c>IndexOutOfRangeException</c> from <c>KeyNotFoundException</c> is which one a
        /// <c>catch</c> clause names, and that is the class itself. The constructor is declared per
        /// subclass rather than inherited because constructors are never inherited - the same rule
        /// the metadata already enforces.
        /// </remarks>
        internal static void DeclareExceptionSubclass(SurtrBuiltInTypeBuilder builder)
        {
            builder.Constructor(
                SurtrNativeEntryPoint.FromFunctionPointer(&ExceptionConstruct),
                builder.Params(("message", SurtrClassReference.String)));
        }

        private static int ExceptionConstruct(SurtrCallArguments arguments)
        {
            // arguments[0] is the receiver, as it is for every instance member.
            arguments.GetUnchecked<SurtrInstance>(0)[MessageSlot] = arguments.GetValueUnchecked(1);
            return arguments.Return(SurtrValue.Null);
        }

        private static int ExceptionMessage(SurtrCallArguments arguments)
            => arguments.Return(arguments.GetUnchecked<SurtrInstance>(0)[MessageSlot]);

        private static int ExceptionToString(SurtrCallArguments arguments)
        {
            var self = arguments.GetUnchecked<SurtrInstance>(0);
            var message = arguments.Runtime.Resolve<SurtrString>(self[MessageSlot]);

            string text = message is null
                ? self.Class.Name
                : self.Class.Name + ": " + message.Value;

            return arguments.Return(SurtrValue.CreateReference(arguments.Runtime.NewString(text).GetSurtrReference()));
        }
        #endregion

        #region Core interfaces
        /// <summary>
        /// Declares the five interfaces the language leans on, into the built-in module.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>IIterator</c> is the classic two-member cursor rather than a generator, and
        /// deliberately: it is a shape a compiler can pattern-match and lower into a plain indexed
        /// loop for an array, a tuple, a dict or any <c>sealed</c> type, which a generator-based
        /// protocol would not be. The general path exists so the language is uniform; the special
        /// cases exist so the common path costs nothing.
        /// </para>
        /// <para>
        /// Each takes a generic parameter - except <c>IDisposable</c>, which has nothing to
        /// parameterise - and the members name it with the same descriptor form the collections
        /// use. Erased at run time, like every other generic.
        /// </para>
        /// </remarks>
        internal static void DeclareCoreInterfaces(SurtrModule module, SurtrTypeHandleTable handles)
        {
            SurtrClassReference element = SurtrClassReference.GenericParameter(0);

            // Declared before IIterator because IIterator extends it, and an extended contract has
            // to exist before the handle naming it can resolve.
            var disposable = DeclareInterface(module, handles, "IDisposable");
            AddAbstractMethod(disposable, handles, "dispose", SurtrClassReference.Void);

            var iterator = DeclareInterface(module, handles, "IIterator", "T");
            iterator.SetGenericVariance(SurtrGenericVariance.Covariant);

            // A cursor is disposable, which is C#'s decision about `IEnumerator<T>` and taken for
            // C#'s reason: deterministic close of a lazy sequence is a consequence of the cursor
            // being disposable, not a mechanism beside it. It is also what lets a `for-in` know
            // statically that it has something to close, with no run-time question per loop - and
            // what stops a generator travelling as an `IIterable<T>` from escaping the close, since
            // `iterate()` is declared to hand back this contract rather than a concrete class.
            // See docs/Plan-Disposicion.md §3.2.
            iterator.SetDeclaredExtendedInterfaces(new[] { handles.GetOrAdd(disposable.SelfReference) });

            AddAbstractMethod(iterator, handles, "moveNext", SurtrClassReference.Boolean);
            AddAbstractProperty(iterator, handles, "current", element);

            var iterable = DeclareInterface(module, handles, "IIterable", "T");

            // Both cursors only produce their element - moveNext answers whether there is one,
            // current and iterate hand them out - so `out T` is exactly the promise each keeps,
            // and a collection of Circle iterates as a collection of Shape.
            iterable.SetGenericVariance(SurtrGenericVariance.Covariant);
            AddAbstractMethod(iterable, handles, "iterate", iterator.SelfReference);

            // compareTo and equals consume their element and produce nothing of it, so `in T`
            // says what they are: a comparer of Shape compares Circles just as well.
            var comparable = DeclareInterface(module, handles, "IComparable", "T");
            comparable.SetGenericVariance(SurtrGenericVariance.Contravariant);
            AddAbstractMethod(comparable, handles, "compareTo", SurtrClassReference.Integer, ("other", element));

            var equatable = DeclareInterface(module, handles, "IEquatable", "T");
            equatable.SetGenericVariance(SurtrGenericVariance.Contravariant);
            AddAbstractMethod(equatable, handles, "equals", SurtrClassReference.Boolean, ("other", element));
        }

        /// <summary>
        /// The descriptor naming one of the core contracts, with its arity mangled into the name
        /// and its own type parameters supplied as arguments.
        /// </summary>
        /// <remarks>
        /// A generic contract's self reference is the <em>constructed</em> form
        /// (<c>Osurtr:IIterable`1;G0</c>), not an open one. Arity lives in the name and says how
        /// many argument descriptors follow, so a name promising one argument and supplying none is
        /// simply malformed - there is no open form to write.
        /// </remarks>
        internal static SurtrClassReference ContractReference(string name, int arity)
        {
            string fullName = SurtrBuiltIns.ModulePath
                + SurtrClassReference.ModuleSeparator
                + SurtrClassReference.MangleArity(name, arity);

            if (arity == 0)
                return SurtrClassReference.Object(fullName);

            var arguments = new SurtrClassReference[arity];
            for (int i = 0; i < arity; i++)
                arguments[i] = SurtrClassReference.GenericParameter(i);

            return SurtrClassReference.Constructed(fullName, arguments);
        }

        /// <summary>
        /// The descriptor naming one of the core contracts for a concrete argument â€” the form a
        /// built-in declares when it satisfies the contract for its own type
        /// (<c>Osurtr:IEquatable`1;I</c> for <c>int</c>, <c>Osurtr:IEquatable`1;DG0G1</c> for a
        /// dict), as opposed to <see cref="ContractReference(string, int)"/>'s open form.
        /// </summary>
        internal static SurtrClassReference ContractReference(string name, params SurtrClassReference[] arguments)
        {
            string fullName = SurtrBuiltIns.ModulePath
                + SurtrClassReference.ModuleSeparator
                + SurtrClassReference.MangleArity(name, arguments.Length);

            if (arguments.Length == 0)
                return SurtrClassReference.Object(fullName);

            return SurtrClassReference.Constructed(fullName, arguments);
        }

        private static SurtrInterface DeclareInterface(
            SurtrModule module,
            SurtrTypeHandleTable handles,
            string name,
            params string[] genericParameters)
        {
            var contract = new SurtrInterface(
                SurtrClassReference.MangleArity(name, genericParameters.Length),
                ContractReference(name, genericParameters.Length),
                SurtrVisibility.Public,
                declaringType: null);

            contract.SetGenericParameters(genericParameters);

            var selfHandle = handles.GetOrAdd(contract.SelfReference);
            if (!selfHandle.IsResolved)
                selfHandle.Resolve(contract);

            module.AddInterface(contract);
            return contract;
        }

        private static void AddAbstractMethod(
            SurtrInterface contract,
            SurtrTypeHandleTable handles,
            string name,
            SurtrClassReference returnType,
            params (string Name, SurtrClassReference Type)[] parameters)
        {
            var declared = new SurtrParameterInfo[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
                declared[i] = new SurtrParameterInfo(parameters[i].Name, handles.GetOrAdd(parameters[i].Type));

            contract.AddMethod(new SurtrAbstractMethodInfo(
                name,
                handles.GetOrAdd(returnType),
                declared,
                SurtrVisibility.Public,
                handles.GetOrAdd(contract.SelfReference)));
        }

        private static void AddAbstractProperty(
            SurtrInterface contract,
            SurtrTypeHandleTable handles,
            string name,
            SurtrClassReference propertyType)
        {
            var selfHandle = handles.GetOrAdd(contract.SelfReference);

            // Get-only: an iterator's cursor is read, never assigned through the contract.
            var getter = new SurtrAbstractMethodInfo(
                "get_" + name,
                handles.GetOrAdd(propertyType),
                Array.Empty<SurtrParameterInfo>(),
                SurtrVisibility.Public,
                selfHandle);

            contract.AddMethod(getter);
            contract.AddProperty(new SurtrPropertyInfo(
                name,
                handles.GetOrAdd(propertyType),
                getter,
                setter: null,
                isStatic: false,
                SurtrVisibility.Public,
                selfHandle));
        }
        #endregion
    }
}
