#nullable enable

using System;
using System.Runtime.CompilerServices;

namespace Surtr.Runtime.Utilities
{
    public interface IRuntimeResource : IDisposable
    {
        RuntimeResourceState ResourceState { get; }

        void Initialize();
    }

    public interface IRuntimeResource<InitType> : IDisposable
    {
        RuntimeResourceState ResourceState { get; }

        void Initialize(InitType initValue);
    }

    public interface IRuntimeResource<InitType1, InitType2> : IDisposable
    {
        RuntimeResourceState ResourceState { get; }

        void Initialize(InitType1 initValue1, InitType2 initValue2);
    }

    public enum RuntimeResourceState
    {
        Uninitialized   = 0,
        Initialized     = 1,
        Disposed        = 2
    }

    public static class RuntimeResourceExtensions
    {
        extension(RuntimeResourceState state)
        {
            public bool IsUnitialized
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => state == RuntimeResourceState.Uninitialized;
            }

            public bool IsInitialized
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => state == RuntimeResourceState.Initialized;
            }

            public bool IsDisposed
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => state == RuntimeResourceState.Disposed;
            }
        }
    }
}
