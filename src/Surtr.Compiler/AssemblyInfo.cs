#nullable enable

using System.Runtime.CompilerServices;

// The declaration-filling half of the symbol model is internal on purpose: a type's base class,
// interfaces, members and type parameters are written once by the binder's phases and read by
// everything else, so exposing the setters would invite a consumer to mutate a symbol after the
// phase that owns it has finished. Surtr.Tests is the one consumer that legitimately builds
// symbols by hand, since there is no binder yet to build them for it.
[assembly: InternalsVisibleTo("Surtr.Tests")]
