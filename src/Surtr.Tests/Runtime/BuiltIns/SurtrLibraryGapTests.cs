#nullable enable

using Surtr.Bytecode.Emit;
using Surtr.Runtime;
using Surtr.Runtime.BuiltIns;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using Surtr.VM;
using System;

namespace Surtr.Tests.Runtime.BuiltIns
{
    /// <summary>
    /// Covers the members <c>docs/Language-Syntax.md</c> writes out in its examples and the
    /// library did not have: <c>array.sort</c> (§8) and <c>string.format</c>.
    /// </summary>
    public class SurtrLibraryGapTests
    {
        private static SurtrMethodInfo MemberOf(SurtrClass type, string name)
        {
            SurtrBuiltIns.EnsureBuilt();
            Assert.True(type.TryGetMethods(name, out var overloads), $"{type.Name} declares no '{name}'.");
            return overloads[0];
        }

        #region array.sort

        /// <summary>
        /// Builds a comparator as a real Surtr closure over bytecode, so sorting genuinely
        /// re-enters the VM per comparison the way a `(a, b) =&gt; ...` lambda would.
        /// </summary>
        private static SurtrClosure Comparator(SurtrRuntime runtime, string modulePath, bool descending = false)
        {
            var builder = new SurtrModuleBuilder(modulePath);

            var compare = builder.DefineFunction(
                "compare",
                SurtrClassReference.Integer,
                new[]
                {
                    builder.Parameter("a", SurtrClassReference.Integer),
                    builder.Parameter("b", SurtrClassReference.Integer),
                });

            // a - b ascending, b - a descending.
            compare.Code
                .LoadLocal(compare.Parameter(descending ? 1 : 0))
                .LoadLocal(compare.Parameter(descending ? 0 : 1))
                .Sub()
                .ReturnValue();

            runtime.LoadModule(builder.Build());
            return runtime.NewClosure(compare.Built!);
        }

        private static SurtrArray ArrayOf(SurtrRuntime runtime, params int[] values)
        {
            var array = runtime.NewArray(SurtrClassReference.Array(SurtrClassReference.Integer));
            foreach (int value in values)
                array.Add(SurtrValue.CreateInt(value));

            return array;
        }

        private static int[] Contents(SurtrArray array)
        {
            var values = new int[array.Length];
            for (int i = 0; i < values.Length; i++)
                values[i] = array[i].AsInt;

            return values;
        }

        [Fact]
        public void Sort_OrdersThroughTheComparator()
        {
            using var runtime = new SurtrRuntime();

            var items = ArrayOf(runtime, 5, 3, 9, 1, 4, 1, 8);
            var comparator = Comparator(runtime, "cmp");

            runtime.Invoke(MemberOf(SurtrBuiltIns.Array, "sort"), runtime.ValueOf(items), runtime.ValueOf(comparator));

            Assert.Equal(new[] { 1, 1, 3, 4, 5, 8, 9 }, Contents(items));
        }

        [Fact]
        public void Sort_FollowsTheComparatorsDirection()
        {
            using var runtime = new SurtrRuntime();

            var items = ArrayOf(runtime, 5, 3, 9, 1);
            var comparator = Comparator(runtime, "cmp", descending: true);

            runtime.Invoke(MemberOf(SurtrBuiltIns.Array, "sort"), runtime.ValueOf(items), runtime.ValueOf(comparator));

            Assert.Equal(new[] { 9, 5, 3, 1 }, Contents(items));
        }

        /// <summary>
        /// Stability is the reason this is a merge sort rather than <c>Array.Sort</c>: a script
        /// sorting by one field of several depends on equal elements keeping their order.
        /// </summary>
        [Fact]
        public void Sort_IsStable()
        {
            using var runtime = new SurtrRuntime();

            // Every element compares equal, so a stable sort has to leave the array untouched.
            var builder = new SurtrModuleBuilder("tie");
            var compare = builder.DefineFunction(
                "compare",
                SurtrClassReference.Integer,
                new[]
                {
                    builder.Parameter("a", SurtrClassReference.Integer),
                    builder.Parameter("b", SurtrClassReference.Integer),
                });

            compare.Code.LoadInt(0).ReturnValue();
            runtime.LoadModule(builder.Build());

            var items = ArrayOf(runtime, 4, 1, 3, 2, 5, 0, 7, 6);
            var comparator = runtime.NewClosure(compare.Built!);

            runtime.Invoke(MemberOf(SurtrBuiltIns.Array, "sort"), runtime.ValueOf(items), runtime.ValueOf(comparator));

            Assert.Equal(new[] { 4, 1, 3, 2, 5, 0, 7, 6 }, Contents(items));
        }

        [Theory]
        [InlineData(new int[0])]
        [InlineData(new[] { 7 })]
        [InlineData(new[] { 2, 1 })]
        public void Sort_HandlesShortArrays(int[] values)
        {
            using var runtime = new SurtrRuntime();

            var items = ArrayOf(runtime, values);
            var comparator = Comparator(runtime, "cmp");

            runtime.Invoke(MemberOf(SurtrBuiltIns.Array, "sort"), runtime.ValueOf(items), runtime.ValueOf(comparator));

            var expected = (int[])values.Clone();
            Array.Sort(expected);
            Assert.Equal(expected, Contents(items));
        }

        /// <summary>An odd length exercises the merge's uneven final run.</summary>
        [Fact]
        public void Sort_HandlesALengthThatIsNotAPowerOfTwo()
        {
            using var runtime = new SurtrRuntime();

            var values = new[] { 9, 2, 7, 4, 1, 8, 3, 6, 5 };
            var items = ArrayOf(runtime, values);
            var comparator = Comparator(runtime, "cmp");

            runtime.Invoke(MemberOf(SurtrBuiltIns.Array, "sort"), runtime.ValueOf(items), runtime.ValueOf(comparator));

            Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, Contents(items));
        }

        #endregion

        #region string.format

        private static string Format(SurtrRuntime runtime, string pattern, params string[] args)
        {
            var packed = runtime.NewArray(SurtrClassReference.Array(SurtrClassReference.String));
            foreach (string argument in args)
                packed.Add(runtime.ValueOf(runtime.NewString(argument)));

            var result = runtime.Invoke(
                MemberOf(SurtrBuiltIns.String, "format"),
                runtime.ValueOf(runtime.NewString(pattern)),
                runtime.ValueOf(packed));

            return runtime.Resolve<SurtrString>(result)!.Text;
        }

        [Fact]
        public void Format_SubstitutesByIndex()
        {
            using var runtime = new SurtrRuntime();

            Assert.Equal("a b", Format(runtime, "{0} {1}", "a", "b"));
            Assert.Equal("b a", Format(runtime, "{1} {0}", "a", "b"));
            Assert.Equal("aa", Format(runtime, "{0}{0}", "a"));
            Assert.Equal("no holes", Format(runtime, "no holes"));
        }

        [Fact]
        public void Format_TakesADoubledBraceAsALiteralOne()
        {
            using var runtime = new SurtrRuntime();

            Assert.Equal("{0}", Format(runtime, "{{0}}", "x"));
            Assert.Equal("{x}", Format(runtime, "{{{0}}}", "x"));
            Assert.Equal("}", Format(runtime, "}"));
        }

        [Fact]
        public void Format_ReadsMultiDigitIndices()
        {
            using var runtime = new SurtrRuntime();

            var args = new string[11];
            for (int i = 0; i < args.Length; i++)
                args[i] = i.ToString();

            Assert.Equal("10", Format(runtime, "{10}", args));
        }

        /// <summary>
        /// A pattern and its arguments drifting apart is a bug, so it is reported rather than
        /// printed as nothing.
        /// </summary>
        /// <remarks>
        /// It surfaces here as the CLR exception itself, because a host calling straight into a
        /// native member has no Surtr frame between it and the throw. Reached from Surtr code the
        /// same throw is wrapped as the library's <c>ArgumentException</c> and a <c>catch</c> can
        /// name it — <c>SurtrBuiltIns.ExceptionClassFor</c> is what pairs the two.
        /// </remarks>
        [Fact]
        public void Format_RejectsAnIndexWithNoArgument()
        {
            using var runtime = new SurtrRuntime();

            Assert.Throws<ArgumentException>(() => Format(runtime, "{2}", "a"));
            Assert.Same(SurtrBuiltIns.ArgumentException, SurtrBuiltIns.ExceptionClassFor(new ArgumentException("x")));
        }

        [Fact]
        public void Format_RejectsAMalformedPlaceholder()
        {
            using var runtime = new SurtrRuntime();

            Assert.Throws<ArgumentException>(() => Format(runtime, "{a}", "x"));
            Assert.Throws<ArgumentException>(() => Format(runtime, "{0", "x"));
        }

        #endregion
    }
}
