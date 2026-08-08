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
