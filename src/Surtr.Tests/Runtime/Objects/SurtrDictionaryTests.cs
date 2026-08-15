#nullable enable

using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;

namespace Surtr.Tests.Runtime.Objects
{
    public class SurtrDictionaryTests
    {
        private static SurtrClassReference DictType => SurtrClassReference.Dictionary(SurtrClassReference.String, SurtrClassReference.Integer);

        [Fact]
        public void NewDictionary_StartsEmpty()
        {
            using var runtime = new SurtrRuntime();
            var dict = runtime.NewDictionary(DictType);

            Assert.Equal(0, dict.Count);
            Assert.True(dict.IsEmpty);
        }

        [Fact]
        public void SetThenTryGet_RoundTrips()
        {
            using var runtime = new SurtrRuntime();
            var dict = runtime.NewDictionary(DictType);
            SurtrValue key = runtime.ValueOf(runtime.NewString("key"));

            dict.Set(key, SurtrValue.CreateInt(42));

            Assert.True(dict.TryGet(key, out var value));
            Assert.Equal(42, value.AsInt);
        }

        [Fact]
        public void TryGet_MissingKey_ReturnsFalse()
        {
            using var runtime = new SurtrRuntime();
            var dict = runtime.NewDictionary(DictType);

            Assert.False(dict.TryGet(runtime.ValueOf(runtime.NewString("missing")), out _));
        }

        [Fact]
        public void Keys_AreCompared_ByValueNotIdentity()
        {
            using var runtime = new SurtrRuntime();
            var dict = runtime.NewDictionary(DictType);

            dict.Set(runtime.ValueOf(runtime.NewString("hello")), SurtrValue.CreateInt(1));

            // A distinct string object holding the same text must find the same entry.
            Assert.True(dict.ContainsKey(runtime.ValueOf(runtime.NewString("hello"))));
        }

        [Fact]
        public void Set_OnAnExistingKey_Replaces()
        {
            using var runtime = new SurtrRuntime();
            var dict = runtime.NewDictionary(DictType);
            SurtrValue key = runtime.ValueOf(runtime.NewString("key"));

            dict.Set(key, SurtrValue.CreateInt(1));
            dict.Set(key, SurtrValue.CreateInt(2));

            Assert.Equal(1, dict.Count);
            Assert.True(dict.TryGet(key, out var value));
            Assert.Equal(2, value.AsInt);
        }

        [Fact]
        public void Remove_DropsTheEntry()
        {
            using var runtime = new SurtrRuntime();
            var dict = runtime.NewDictionary(DictType);
            SurtrValue key = runtime.ValueOf(runtime.NewString("key"));
            dict.Set(key, SurtrValue.CreateInt(1));

            Assert.True(dict.Remove(key));
            Assert.False(dict.ContainsKey(key));
            Assert.False(dict.Remove(key));
        }

        [Fact]
        public void Clear_DropsEveryEntry()
        {
            using var runtime = new SurtrRuntime();
            var dict = runtime.NewDictionary(DictType);
            dict.Set(runtime.ValueOf(runtime.NewString("a")), SurtrValue.CreateInt(1));
            dict.Set(runtime.ValueOf(runtime.NewString("b")), SurtrValue.CreateInt(2));

            dict.Clear();

            Assert.Equal(0, dict.Count);
        }

        [Fact]
        public void CopyKeysTo_AppendsEveryKey()
        {
            using var runtime = new SurtrRuntime();
            var dict = runtime.NewDictionary(DictType);
            dict.Set(runtime.ValueOf(runtime.NewString("a")), SurtrValue.CreateInt(1));
            dict.Set(runtime.ValueOf(runtime.NewString("b")), SurtrValue.CreateInt(2));

            var destination = runtime.NewArray(SurtrClassReference.Array(SurtrClassReference.String));
            dict.CopyKeysTo(destination);

            Assert.Equal(dict.Count, destination.Length);
        }

        [Fact]
        public void CopyValuesTo_AppendsEveryValue()
        {
            using var runtime = new SurtrRuntime();
            var dict = runtime.NewDictionary(DictType);
            dict.Set(runtime.ValueOf(runtime.NewString("a")), SurtrValue.CreateInt(1));
            dict.Set(runtime.ValueOf(runtime.NewString("b")), SurtrValue.CreateInt(2));

            var destination = runtime.NewArray(SurtrClassReference.Array(SurtrClassReference.Integer));
            dict.CopyValuesTo(destination);

            Assert.Equal(dict.Count, destination.Length);
        }

        [Fact]
        public void KeyAndValueTypes_AreSlicedFromTheTypeReference()
        {
            using var runtime = new SurtrRuntime();
            var dict = runtime.NewDictionary(DictType);

            Assert.Equal(SurtrClassReference.String, dict.KeyType);
            Assert.Equal(SurtrClassReference.Integer, dict.ValueType);
        }

        #region The int-specialised store
        private static SurtrClassReference IntKeyedType => SurtrClassReference.Dictionary(SurtrClassReference.Integer, SurtrClassReference.Integer);

        [Fact]
        public void AnIntKeyedDictionary_SpecializesItsStorage()
        {
            using var runtime = new SurtrRuntime();

            Assert.True(runtime.NewDictionary(IntKeyedType).IsIntSpecialized);
            Assert.False(runtime.NewDictionary(DictType).IsIntSpecialized);

            // A nullable key stays on the general store: the absent tag is an ordinary key there,
            // and it is not an int.
            var nullableKeyed = SurtrClassReference.Dictionary(
                SurtrClassReference.Nullable(SurtrClassReference.Integer),
                SurtrClassReference.Integer);

            Assert.False(runtime.NewDictionary(nullableKeyed).IsIntSpecialized);

            // And a dictionary with no declared type at all has nothing to specialise on.
            Assert.False(runtime.NewDictionary().IsIntSpecialized);
        }

        [Fact]
        public void TheSpecializedStore_RoundTripsEveryOperation()
        {
            using var runtime = new SurtrRuntime();
            var dict = runtime.NewDictionary(IntKeyedType);

            dict.Set(SurtrValue.CreateInt(7), SurtrValue.CreateInt(70));
            dict.Set(SurtrValue.CreateInt(-3), SurtrValue.CreateInt(-30));
            dict.Set(SurtrValue.CreateInt(7), SurtrValue.CreateInt(71));

            Assert.Equal(2, dict.Count);
            Assert.True(dict.ContainsKey(SurtrValue.CreateInt(7)));
            Assert.True(dict.TryGet(SurtrValue.CreateInt(7), out var value));
            Assert.Equal(71, value.AsInt);

            Assert.True(dict.Remove(SurtrValue.CreateInt(-3)));
            Assert.False(dict.Remove(SurtrValue.CreateInt(-3)));
            Assert.False(dict.ContainsKey(SurtrValue.CreateInt(-3)));

            dict.Clear();
            Assert.True(dict.IsEmpty);
        }

        [Fact]
        public void ABoxedInt_IsTheSameKeyAsAnUnboxedOne_OnTheSpecializedStore()
        {
            using var runtime = new SurtrRuntime();
            var dict = runtime.NewDictionary(IntKeyedType);
            SurtrValue boxed = runtime.ValueOf(runtime.Box(SurtrValue.CreateInt(5)));

            // Stored boxed, found unboxed - and the store must not have de-specialised to do it.
            dict.Set(boxed, SurtrValue.CreateInt(50));

            Assert.True(dict.IsIntSpecialized);
            Assert.Equal(1, dict.Count);
            Assert.True(dict.TryGet(SurtrValue.CreateInt(5), out var value));
            Assert.Equal(50, value.AsInt);

            // And the other direction: stored unboxed, found boxed.
            dict.Set(SurtrValue.CreateInt(6), SurtrValue.CreateInt(60));

            Assert.True(dict.ContainsKey(runtime.ValueOf(runtime.Box(SurtrValue.CreateInt(6)))));
            Assert.True(dict.Remove(runtime.ValueOf(runtime.Box(SurtrValue.CreateInt(6)))));
            Assert.Equal(1, dict.Count);
        }

        [Fact]
        public void AKeyTheSpecializedStoreCannotHold_DeoptimizesRatherThanChangingSemantics()
        {
            using var runtime = new SurtrRuntime();
            var dict = runtime.NewDictionary(IntKeyedType);

            dict.Set(SurtrValue.CreateInt(1), SurtrValue.CreateInt(10));
            dict.Set(SurtrValue.CreateInt(2), SurtrValue.CreateInt(20));

            // A host is not bound by the declared key type. A char 5 is not an int 5, so the
            // specialisation cannot represent it and has to be given up.
            dict.Set(SurtrValue.CreateChar('a'), SurtrValue.CreateInt(30));

            Assert.False(dict.IsIntSpecialized);
            Assert.Equal(3, dict.Count);

            // The entries that were already there came across, and still answer to an int key.
            Assert.True(dict.TryGet(SurtrValue.CreateInt(1), out var first));
            Assert.Equal(10, first.AsInt);
            Assert.True(dict.TryGet(SurtrValue.CreateChar('a'), out var third));
            Assert.Equal(30, third.AsInt);

            // Insertion order survives the migration, so keys() answers the same either side of it.
            var keys = runtime.NewArray(SurtrClassReference.Array(SurtrClassReference.Integer));
            dict.CopyKeysTo(keys);

            Assert.Equal(1, keys[0].AsInt);
            Assert.Equal(2, keys[1].AsInt);
            Assert.Equal('a', keys[2].AsChar);
        }

        [Fact]
        public void AValueClassBox_IsNotAnIntKey()
        {
            using var runtime = new SurtrRuntime();
            var dict = runtime.NewDictionary(IntKeyedType);
            dict.Set(SurtrValue.CreateInt(7), SurtrValue.CreateInt(70));

            // A value class wrapping an int also holds an int and is a distinct type - EntityId(7)
            // is not 7, and unwrapping it onto the specialised store would alias the two.
            var entityId = new SurtrClass(
                "EntityId",
                SurtrValueTypeCode.Object,
                SurtrClassReference.Object("test:EntityId"),
                baseType: null,
                isAbstract: false,
                SurtrVisibility.Public,
                declaringType: null);

            var boxed = new SurtrBoxed(entityId, SurtrValue.CreateInt(7));
            runtime.Context.EntityRegistry.Register(boxed);
            SurtrValue key = runtime.ValueOf(boxed);

            Assert.False(dict.ContainsKey(key));
            Assert.True(dict.IsIntSpecialized);

            dict.Set(key, SurtrValue.CreateInt(700));

            Assert.False(dict.IsIntSpecialized);
            Assert.Equal(2, dict.Count);
            Assert.True(dict.TryGet(SurtrValue.CreateInt(7), out var byInt));
            Assert.Equal(70, byInt.AsInt);
            Assert.True(dict.TryGet(key, out var byId));
            Assert.Equal(700, byId.AsInt);
        }

        [Fact]
        public void TheSpecializedStore_TracesItsValues()
        {
            using var runtime = new SurtrRuntime();
            var dict = runtime.NewDictionary(SurtrClassReference.Dictionary(
                SurtrClassReference.Integer, SurtrClassReference.String));

            var value = runtime.NewString("v");
            SurtrValue valueRef = SurtrValue.CreateReference(value.GetSurtrReference());
            dict.Set(SurtrValue.CreateInt(1), runtime.ValueOf(value));

            runtime.AddRoot(dict);
            runtime.Collect();

            Assert.NotNull(runtime.Resolve<SurtrString>(valueRef));

            dict.Clear();
            runtime.Collect();

            Assert.Null(runtime.Resolve<SurtrString>(valueRef));
        }

        [Fact]
        public void SnapshotKeys_ReadsEitherStore_InOrder()
        {
            using var runtime = new SurtrRuntime();

            var specialized = runtime.NewDictionary(IntKeyedType);
            specialized.Set(SurtrValue.CreateInt(3), SurtrValue.CreateInt(0));
            specialized.Set(SurtrValue.CreateInt(1), SurtrValue.CreateInt(0));

            var keys = specialized.SnapshotKeys();
            Assert.Equal(new[] { 3, 1 }, new[] { keys[0].AsInt, keys[1].AsInt });

            var general = runtime.NewDictionary(DictType);
            general.Set(runtime.ValueOf(runtime.NewString("a")), SurtrValue.CreateInt(0));

            Assert.Single(general.SnapshotKeys());
        }
        #endregion

        [Fact]
        public void VisitReferences_KeepsBothKeysAndValuesAlive()
        {
            using var runtime = new SurtrRuntime();
            var keyDictType = SurtrClassReference.Dictionary(SurtrClassReference.String, SurtrClassReference.String);
            var dict = runtime.NewDictionary(keyDictType);

            var key = runtime.NewString("k");
            var value = runtime.NewString("v");
            SurtrValue keyRef = SurtrValue.CreateReference(key.GetSurtrReference());
            SurtrValue valueRef = SurtrValue.CreateReference(value.GetSurtrReference());

            dict.Set(runtime.ValueOf(key), runtime.ValueOf(value));

            runtime.AddRoot(dict);
            runtime.Collect();

            Assert.NotNull(runtime.Resolve<SurtrString>(keyRef));
            Assert.NotNull(runtime.Resolve<SurtrString>(valueRef));

            dict.Clear();
            runtime.Collect();

            Assert.Null(runtime.Resolve<SurtrString>(keyRef));
            Assert.Null(runtime.Resolve<SurtrString>(valueRef));
        }
    }
}
