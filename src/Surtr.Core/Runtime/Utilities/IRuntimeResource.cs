#nullable enable

using System;
using System.Runtime.CompilerServices;

namespace Surtr.Runtime.Utilities
{
    /// <summary>
    /// A resource with an explicit init/dispose lifecycle (as opposed to being ready to
    /// use right after construction), tracked via <see cref="ResourceState"/>.
    /// </summary>
    public interface IRuntimeResource : IDisposable
    {
        /// <summary>The resource's current lifecycle state.</summary>
        RuntimeResourceState ResourceState { get; }

        /// <summary>Brings the resource from <see cref="RuntimeResourceState.Uninitialized"/> into <see cref="RuntimeResourceState.Initialized"/>.</summary>
        void Initialize();
    }

    /// <summary>Same contract as <see cref="IRuntimeResource"/>, but <see cref="Initialize(InitType)"/> takes a single configuration value.</summary>
    public interface IRuntimeResource<InitType> : IDisposable
    {
        /// <summary>The resource's current lifecycle state.</summary>
        RuntimeResourceState ResourceState { get; }

        /// <summary>Brings the resource from <see cref="RuntimeResourceState.Uninitialized"/> into <see cref="RuntimeResourceState.Initialized"/>.</summary>
        void Initialize(InitType initValue);
    }

    /// <summary>Same contract as <see cref="IRuntimeResource"/>, but <see cref="Initialize(InitType1, InitType2)"/> takes two configuration values.</summary>
    public interface IRuntimeResource<InitType1, InitType2> : IDisposable
    {
        /// <summary>The resource's current lifecycle state.</summary>
        RuntimeResourceState ResourceState { get; }

        /// <summary>Brings the resource from <see cref="RuntimeResourceState.Uninitialized"/> into <see cref="RuntimeResourceState.Initialized"/>.</summary>
        void Initialize(InitType1 initValue1, InitType2 initValue2);
    }

    /// <summary>The lifecycle state of an <see cref="IRuntimeResource"/>.</summary>
    public enum RuntimeResourceState
    {
        /// <summary>Constructed but not yet initialized; not safe to use.</summary>
        Uninitialized   = 0,

        /// <summary>Initialized and safe to use.</summary>
        Initialized     = 1,

        /// <summary>Disposed; no longer safe to use.</summary>
        Disposed        = 2
    }

    /// <summary>Convenience predicates over <see cref="RuntimeResourceState"/>.</summary>
    public static class RuntimeResourceExtensions
    {
        extension(RuntimeResourceState state)
        {
            /// <summary>Whether the state is <see cref="RuntimeResourceState.Uninitialized"/>.</summary>
            public bool IsUnitialized
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => state == RuntimeResourceState.Uninitialized;
            }

            /// <summary>Whether the state is <see cref="RuntimeResourceState.Initialized"/>.</summary>
            public bool IsInitialized
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => state == RuntimeResourceState.Initialized;
            }

            /// <summary>Whether the state is <see cref="RuntimeResourceState.Disposed"/>.</summary>
            public bool IsDisposed
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => state == RuntimeResourceState.Disposed;
            }
        }
    }
}
