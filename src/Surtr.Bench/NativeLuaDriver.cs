#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Surtr.Bench
{
    /// <summary>
    /// Drives a native Lua 5.1-compatible library through the C API — here LuaJIT's
    /// <c>lua51.dll</c> from the <c>luajit.native</c> package, copied beside the harness. Functions
    /// are resolved once at load and each workload's function is kept as a registry reference, so a
    /// timed loop pays the C call, never a name lookup or a marshalled string.
    /// </summary>
    /// <remarks>
    /// The Lua 5.1 API makes several entry points macros rather than exported functions
    /// (<c>lua_getglobal</c>, <c>lua_pushinteger</c>, <c>lua_tonumber</c>, <c>lua_pop</c>), so the
    /// P/Invoke surface names the real functions they expand to: <c>lua_getfield</c> with
    /// <c>LUA_GLOBALSINDEX</c>, <c>lua_pushnumber</c>, <c>lua_tonumberx</c> and <c>lua_settop</c>.
    /// </remarks>
    internal sealed class NativeLuaDriver : IBenchEngine, IDisposable
    {
        private const string Library = "lua51";
        private const int LuaGlobalsIndex = -10002;
        private const int LuaRegistryIndex = -10000;

        private IntPtr _state;
        private readonly Dictionary<string, int> _functionRefs = new();

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr luaL_newstate();

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern void luaL_openlibs(IntPtr state);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int luaL_loadstring(IntPtr state, string chunk);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int lua_pcall(IntPtr state, int nargs, int nresults, int errorFunction);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern void lua_getfield(IntPtr state, int index, string name);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern void lua_pushnumber(IntPtr state, double number);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern double lua_tonumberx(IntPtr state, int index, IntPtr isNumber);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int luaL_ref(IntPtr state, int table);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern void lua_rawgeti(IntPtr state, int index, int reference);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern void lua_settop(IntPtr state, int index);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr lua_tolstring(IntPtr state, int index, IntPtr length);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern void lua_close(IntPtr state);

        public string Name => "luajit";

        public static NativeLuaDriver Load(string source)
        {
            var driver = new NativeLuaDriver();
            driver.Initialize(source);
            return driver;
        }

        private void Initialize(string source)
        {
            _state = luaL_newstate();
            if (_state == IntPtr.Zero)
                throw new InvalidOperationException("LuaJIT could not allocate a state.");

            luaL_openlibs(_state);

            if (luaL_loadstring(_state, source) != 0)
                throw new InvalidOperationException("LuaJIT could not compile the chunk: " + LastError());
            if (lua_pcall(_state, 0, 0, 0) != 0)
                throw new InvalidOperationException("LuaJIT could not run the chunk: " + LastError());

            foreach (var workload in Workloads.AllWorkloads)
            {
                lua_getfield(_state, LuaGlobalsIndex, workload.Name);
                int reference = luaL_ref(_state, LuaRegistryIndex);
                if (reference == -1)
                    throw new InvalidOperationException($"the LuaJIT chunk defines no '{workload.Name}'.");
                _functionRefs[workload.Name] = reference;
            }
        }

        public double Call(Workload workload, long size)
        {
            lua_rawgeti(_state, LuaRegistryIndex, _functionRefs[workload.Name]);
            lua_pushnumber(_state, size);
            if (lua_pcall(_state, 1, 1, 0) != 0)
                throw new InvalidOperationException($"LuaJIT '{workload.Name}' failed: " + LastError());

            double result = lua_tonumberx(_state, -1, IntPtr.Zero);
            lua_settop(_state, -2);
            return result;
        }

        private string LastError()
        {
            IntPtr text = lua_tolstring(_state, -1, IntPtr.Zero);
            lua_settop(_state, -2);
            return text == IntPtr.Zero ? "unknown error" : Marshal.PtrToStringAnsi(text) ?? "unknown error";
        }

        public void Dispose()
        {
            if (_state != IntPtr.Zero)
            {
                lua_close(_state);
                _state = IntPtr.Zero;
            }
        }
    }
}
