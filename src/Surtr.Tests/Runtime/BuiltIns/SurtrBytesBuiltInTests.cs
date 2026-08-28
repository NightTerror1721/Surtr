#nullable enable

using Surtr.Compiler.Binding;
using Surtr.Compiler.CodeGen;
using Surtr.Compiler.Compilation;
using Surtr.Compiler.Diagnostics;
using Surtr.Runtime;
using Surtr.Runtime.BuiltIns;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using Surtr.VM;
using System;
using Xunit;

namespace Surtr.Tests.Runtime.BuiltIns
{
    /// <summary>
    /// The <c>bytes</c> built-in: its descriptor, its class registration, and the members it
    /// exposes to Surtr source - exercised end to end through the compiler and the VM.
    /// </summary>
    public class SurtrBytesBuiltInTests
    {
        #region Descriptor and class registration

        [Fact]
        public void TheBytesClassIsTheTypeCodesSharedClass()
        {
            Assert.Same(SurtrBuiltIns.Bytes, SurtrBuiltIns.ForTypeCode(SurtrValueTypeCode.Bytes));
            Assert.True(SurtrValueTypeCode.Bytes.IsBuiltIn);
            Assert.True(SurtrValueTypeCode.Bytes.IsReferenceType);
            Assert.True(SurtrValueTypeCode.Bytes.IsBytes);
            Assert.True(SurtrBuiltIns.Bytes.IsSubclassOf(SurtrBuiltIns.Bytes));
        }

        [Fact]
        public void TheBytesClassDeclaresItsMemberSurface()
        {
            Assert.True(SurtrBuiltIns.Bytes.TryGetProperty("length", out _));
            Assert.True(SurtrBuiltIns.Bytes.TryGetProperty("capacity", out _));
            Assert.True(SurtrBuiltIns.Bytes.TryGetProperty("isEmpty", out _));

            Assert.True(SurtrBuiltIns.Bytes.TryGetMethods("get", out _));
            Assert.True(SurtrBuiltIns.Bytes.TryGetMethods("set", out _));
            Assert.True(SurtrBuiltIns.Bytes.TryGetMethods("push", out _));
            Assert.True(SurtrBuiltIns.Bytes.TryGetMethods("pop", out _));
            Assert.True(SurtrBuiltIns.Bytes.TryGetMethods("insert", out _));
            Assert.True(SurtrBuiltIns.Bytes.TryGetMethods("removeAt", out _));
            Assert.True(SurtrBuiltIns.Bytes.TryGetMethods("truncate", out _));
            Assert.True(SurtrBuiltIns.Bytes.TryGetMethods("reserve", out _));
            Assert.True(SurtrBuiltIns.Bytes.TryGetMethods("clear", out _));
            Assert.True(SurtrBuiltIns.Bytes.TryGetMethods("reverse", out _));
            Assert.True(SurtrBuiltIns.Bytes.TryGetMethods("indexOf", out _));
            Assert.True(SurtrBuiltIns.Bytes.TryGetMethods("lastIndexOf", out _));
            Assert.True(SurtrBuiltIns.Bytes.TryGetMethods("contains", out _));
            Assert.True(SurtrBuiltIns.Bytes.TryGetMethods("slice", out _));
            Assert.True(SurtrBuiltIns.Bytes.TryGetMethods("concat", out _));
            Assert.True(SurtrBuiltIns.Bytes.TryGetMethods("copy", out _));
            Assert.True(SurtrBuiltIns.Bytes.TryGetMethods("copyFrom", out _));
            Assert.True(SurtrBuiltIns.Bytes.TryGetMethods("copyTo", out _));
            Assert.True(SurtrBuiltIns.Bytes.TryGetMethods("op_[]", out _));
            Assert.True(SurtrBuiltIns.Bytes.TryGetMethods("equals", out _));
            Assert.True(SurtrBuiltIns.Bytes.TryGetMethods("compareTo", out _));
            Assert.True(SurtrBuiltIns.Bytes.TryGetMethods("toString", out _));
            Assert.True(SurtrBuiltIns.Bytes.TryGetMethods("decodeUTF8", out _));
        }

        [Fact]
        public void TheBytesClassSatisfiesTheComparabilityContracts()
        {
            Assert.True(SurtrBuiltIns.Bytes.Implements(Contract("IComparable")));
            Assert.True(SurtrBuiltIns.Bytes.Implements(Contract("IEquatable")));
        }

        private static SurtrInterface Contract(string name)
        {
            Assert.True(
                SurtrBuiltIns.Module.TryGetInterface(SurtrClassReference.MangleArity(name, 1), out var contract),
                $"The built-in module declares no '{name}`1'.");
            return contract;
        }

        [Fact]
        public void Interfaces_DispatchThroughTheVtable()
        {
            // `bytes` satisfies IComparable<bytes>/IEquatable<bytes>; a generic constrained to the
            // contract has to reach the content bodies through interface dispatch, which is exactly
            // what the vtable slot matching is for.
            const string source =
                "fun compare<T : IComparable<T>>(a: T, b: T): int {\n"
                + "    return a.compareTo(b);\n"
                + "}\n"
                + "fun run(): int {\n"
                + "    var x = bytes([1, 2]);\n"
                + "    var y = bytes([1, 3]);\n"
                + "    if (compare(x, y) != -1) { return 1; }\n"
                + "    if (compare(x, bytes([1, 2])) != 0) { return 2; }\n"
                + "    return 0;\n"
                + "}\n";

            Assert.Equal(0, Run(source));
        }

        #endregion

        #region Compile-and-run

        /// <summary>Compiles <paramref name="source"/> to an image, loads it, and runs its <c>run()</c> entry point.</summary>
        private static int Run(string source)
        {
            var project = new SurtrProject(sourceRoot: ".");
            project.AddSourceFile("test.surtr", "test", source);

            using var compilation = SurtrCompilation.Create(project);
            var binder = compilation.Bind();
            binder.BindBodies();

            Assert.True(
                !compilation.Diagnostics.HasErrors,
                "Binding reported: " + string.Join("; ", compilation.Diagnostics));

            var emitter = new ModuleEmitter(compilation, binder);
            Assert.True(emitter.TryEmit());

            using var runtime = new SurtrRuntime();
            runtime.LoadModule(emitter.EmitImages()[0].Instantiate());

            Assert.True(runtime.TryGetModule("test", out var module), "No module 'test' was loaded.");
            Assert.True(module.TryGetMethods("run", out var overloads), "'test' declares no 'run'.");
            return runtime.Invoke(overloads[0]).AsInt;
        }

        [Fact]
        public void Constructors_FromCapacityArrayAndText()
        {
            // bytes() and bytes(int) are empty buffers; bytes(int[]) copies values; bytes(string)
            // is UTF-8.
            const string source =
                "fun run(): int {\n"
                + "    var empty = bytes();\n"
                + "    if (empty.length != 0) { return 1; }\n"
                + "    if (!empty.isEmpty) { return 2; }\n"
                + "    var roomy = bytes(8);\n"
                + "    if (roomy.capacity < 8) { return 3; }\n"
                + "    var b = bytes([1, 2, 3]);\n"
                + "    if (b.length != 3) { return 4; }\n"
                + "    if (b.capacity < 3) { return 5; }\n"
                + "    var text = bytes(\"Hola\");\n"
                + "    if (text.length != 4) { return 6; }\n"
                + "    return 0;\n"
                + "}\n";

            Assert.Equal(0, Run(source));
        }

        [Fact]
        public void ReadWrite_GetSetPushPop()
        {
            const string source =
                "fun run(): int {\n"
                + "    var b = bytes(4);\n"
                + "    b.push(65); b.push(66); b.push(67);\n"
                + "    if (b.length != 3) { return 1; }\n"
                + "    if (b.get(0) != 65 || b.get(2) != 67) { return 2; }\n"
                + "    if (b.pop() != 67) { return 3; }\n"
                + "    if (b.length != 2) { return 4; }\n"
                + "    b.set(1, 99);\n"
                + "    if (b.get(1) != 99) { return 5; }\n"
                + "    return 0;\n"
                + "}\n";

            Assert.Equal(0, Run(source));
        }

        [Fact]
        public void Write_InsertRemoveAtTruncateClearReverse()
        {
            const string source =
                "fun run(): int {\n"
                + "    var b = bytes([1, 2, 3]);\n"
                + "    b.insert(0, 9);\n"
                + "    if (b.get(0) != 9 || b.length != 4) { return 1; }\n"
                + "    b.removeAt(0);\n"
                + "    if (b.get(0) != 1 || b.length != 3) { return 2; }\n"
                + "    b.reverse();\n"
                + "    if (b.get(0) != 3 || b.get(2) != 1) { return 3; }\n"
                + "    b.truncate(2);\n"
                + "    if (b.length != 2) { return 4; }\n"
                + "    b.reserve(64);\n"
                + "    if (b.capacity < 64) { return 5; }\n"
                + "    b.clear();\n"
                + "    if (b.length != 0 || !b.isEmpty) { return 6; }\n"
                + "    return 0;\n"
                + "}\n";

            Assert.Equal(0, Run(source));
        }

        [Fact]
        public void Search_IndexOfLastIndexOfContains()
        {
            const string source =
                "fun run(): int {\n"
                + "    var b = bytes([10, 20, 30, 20]);\n"
                + "    if (b.indexOf(20) != 1) { return 1; }\n"
                + "    if (b.lastIndexOf(20) != 3) { return 2; }\n"
                + "    if (b.indexOf(99) != -1) { return 3; }\n"
                + "    if (!b.contains(30)) { return 4; }\n"
                + "    if (b.contains(31)) { return 5; }\n"
                + "    return 0;\n"
                + "}\n";

            Assert.Equal(0, Run(source));
        }

        [Fact]
        public void Buffering_SliceAndConcat()
        {
            const string source =
                "fun run(): int {\n"
                + "    var b = bytes([1, 2, 3, 4]);\n"
                + "    var win = b.slice(1, 2);\n"
                + "    if (win.length != 2) { return 1; }\n"
                + "    if (win.get(0) != 2 || win.get(1) != 3) { return 2; }\n"
                + "    var joined = b.concat(bytes([5, 6]));\n"
                + "    if (joined.length != 6) { return 3; }\n"
                + "    if (joined.get(4) != 5 || joined.get(5) != 6) { return 4; }\n"
                + "    if (b.length != 4) { return 5; }\n"
                + "    return 0;\n"
                + "}\n";

            Assert.Equal(0, Run(source));
        }

        [Fact]
        public void Equality_OperatorIsIdentityAndEqualsIsContent()
        {
            const string source =
                "fun run(): int {\n"
                + "    var a = bytes([1, 2, 3]);\n"
                + "    var b = bytes([1, 2, 3]);\n"
                + "    if (a == b) { return 1; }\n"
                + "    if (!a.equals(b)) { return 2; }\n"
                + "    if (!a.equals(a)) { return 3; }\n"
                + "    if (a.compareTo(b) != 0) { return 4; }\n"
                + "    if (a.compareTo(bytes([1, 2, 4])) != -1) { return 5; }\n"
                + "    if (a.compareTo(bytes([1, 2])) != 1) { return 6; }\n"
                + "    return 0;\n"
                + "}\n";

            Assert.Equal(0, Run(source));
        }

        [Fact]
        public void Text_Utf8BridgeAndHexToString()
        {
            const string source =
                "fun run(): int {\n"
                + "    var text = bytes(\"ñño\");\n"
                + "    if (text.length != 5) { return 1; }\n"
                + "    if (text.decodeUTF8() != \"ñño\") { return 2; }\n"
                + "    var hex = bytes([65, 66, 255]).toString();\n"
                + "    if (hex != \"4142FF\") { return 3; }\n"
                + "    return 0;\n"
                + "}\n";

            Assert.Equal(0, Run(source));
        }

        [Fact]
        public void Interpolation_UsesTheHexToString()
        {
            const string source =
                "fun run(): int {\n"
                + "    var b = bytes([65, 66]);\n"
                + "    var s = \"packet: ${b}\";\n"
                + "    if (s != \"packet: 4142\") { return 1; }\n"
                + "    return 0;\n"
                + "}\n";

            Assert.Equal(0, Run(source));
        }

        [Fact]
        public void Factories_RepeatFromSliceAndWithCapacity()
        {
            const string source =
                "fun run(): int {\n"
                + "    var filled = bytes.repeat(7, 3);\n"
                + "    if (filled.length != 3) { return 1; }\n"
                + "    if (filled.get(0) != 7 || filled.get(2) != 7) { return 2; }\n"
                + "    var source = [10, 20, 30, 40];\n"
                + "    var win = bytes(source, 1, 2);\n"
                + "    if (win.length != 2) { return 3; }\n"
                + "    if (win.get(0) != 20 || win.get(1) != 30) { return 4; }\n"
                + "    var made = bytes.withCapacity(0);\n"
                + "    if (made.length != 0) { return 5; }\n"
                + "    return 0;\n"
                + "}\n";

            Assert.Equal(0, Run(source));
        }

        [Fact]
        public void OutOfRangeByteValues_AreRejectedAtRuntime()
        {
            // Writing 300 into a byte cannot round-trip; the built-in refuses rather than
            // silently truncating. Uncaught, the host sees the native body's CLR exception raw,
            // exactly like the string built-in's out-of-range index does.
            const string source =
                "fun run(): int {\n"
                + "    var b = bytes();\n"
                + "    b.push(300);\n"
                + "    return 0;\n"
                + "}\n";

            Assert.Throws<ArgumentOutOfRangeException>(() => Run(source));
        }

        [Fact]
        public void Indexer_ReadsAndWritesThroughTheOperator()
        {
            const string source =
                "fun run(): int {\n"
                + "    var b = bytes([10, 20, 30]);\n"
                + "    if (b[0] != 10 || b[2] != 30) { return 1; }\n"
                + "    b[1] = 99;\n"
                + "    if (b[1] != 99) { return 2; }\n"
                + "    if (b.length != 3) { return 3; }\n"
                + "    var sum = b[0] + b[1] + b[2];\n"
                + "    if (sum != 139) { return 4; }\n"
                + "    return 0;\n"
                + "}\n";

            Assert.Equal(0, Run(source));
        }

        [Fact]
        public void Indexer_AnOutOfRangeWriteIsRejected()
        {
            const string source =
                "fun run(): int {\n"
                + "    var b = bytes([1, 2]);\n"
                + "    b[5] = 9;\n"
                + "    return 0;\n"
                + "}\n";

            Assert.Throws<ArgumentOutOfRangeException>(() => Run(source));
        }

        [Fact]
        public void Copy_MakesAnIndependentSnapshot()
        {
            const string source =
                "fun run(): int {\n"
                + "    var b = bytes([1, 2, 3]);\n"
                + "    var snapshot = b.copy();\n"
                + "    if (snapshot.length != 3) { return 1; }\n"
                + "    snapshot[0] = 9;\n"
                + "    if (b[0] != 1) { return 2; }\n"
                + "    if (snapshot[0] != 9) { return 3; }\n"
                + "    return 0;\n"
                + "}\n";

            Assert.Equal(0, Run(source));
        }

        [Fact]
        public void CopyFrom_ReplacesTheContents()
        {
            const string source =
                "fun run(): int {\n"
                + "    var reusable = bytes();\n"
                + "    var source = bytes([5, 6, 7, 8]);\n"
                + "    reusable.copyFrom(source);\n"
                + "    if (reusable.length != 4) { return 1; }\n"
                + "    if (reusable[2] != 7) { return 2; }\n"
                + "    reusable.copyFrom(source, 1, 2);\n"
                + "    if (reusable.length != 2) { return 3; }\n"
                + "    if (reusable[0] != 6 || reusable[1] != 7) { return 4; }\n"
                + "    source[0] = 99;\n"
                + "    if (reusable[0] != 6) { return 5; }\n"
                + "    return 0;\n"
                + "}\n";

            Assert.Equal(0, Run(source));
        }

        [Fact]
        public void CopyTo_WritesIntoTheTargetAndGrowsIt()
        {
            const string source =
                "fun run(): int {\n"
                + "    var payload = bytes([1, 2, 3]);\n"
                + "    var packet = bytes([9, 9, 9]);\n"
                + "    payload.copyTo(packet);\n"
                + "    if (packet.length != 3) { return 1; }\n"
                + "    if (packet[0] != 1 || packet[2] != 3) { return 2; }\n"
                + "    var roomy = bytes([8, 8, 8, 8, 8]);\n"
                + "    payload.copyTo(roomy, 1);\n"
                + "    if (roomy.length != 5) { return 3; }\n"
                + "    if (roomy[0] != 8 || roomy[1] != 1 || roomy[3] != 3) { return 4; }\n"
                + "    if (roomy[4] != 8) { return 7; }\n"
                + "    var growing = bytes();\n"
                + "    payload.copyTo(growing, 5);\n"
                + "    if (growing.length != 8) { return 5; }\n"
                + "    if (growing[5] != 1 || growing[7] != 3) { return 6; }\n"
                + "    return 0;\n"
                + "}\n";

            Assert.Equal(0, Run(source));
        }

        #endregion
    }
}