#nullable enable

using MoonSharp.Interpreter;
using System;
using System.Collections.Generic;

namespace Surtr.Bench
{
    /// <summary>
    /// Loads the Lua chunk into one MoonSharp script and hands back the function values, so a timed
    /// loop pays only the call, never the parse.
    /// </summary>
    internal sealed class LuaDriver : IBenchEngine
    {
        private readonly Script _script;
        private readonly Dictionary<string, DynValue> _functions = new();

        public static LuaDriver Load(string source)
        {
            // Named rather than taken as a preset, so what the Lua side is allowed to reach stays
            // an explicit list. Metatables are here because Lua has no classes: methodCalls,
            // virtualCalls and interfaceCalls dispatch through one, which is what Lua code wanting
            // those things would write and the only honest analogue of the Surtr side. Table is
            // here for table.sort, which sortArray needs.
            var script = new Script(
                CoreModules.Basic
                | CoreModules.TableIterators
                | CoreModules.Table
                | CoreModules.Metatables
                | CoreModules.Math
                | CoreModules.ErrorHandling
                | CoreModules.String);
            script.DoString(source);

            var functions = new Dictionary<string, DynValue>();
            foreach (var workload in Workloads.AllWorkloads)
            {
                DynValue function = script.Globals.Get(workload.Name);
                if (function.IsNil())
                    throw new InvalidOperationException($"the Lua chunk defines no '{workload.Name}'.");
                functions[workload.Name] = function;
            }

            return new LuaDriver(script, functions);
        }

        private LuaDriver(Script script, Dictionary<string, DynValue> functions)
        {
            _script = script;
            _functions = functions;
        }

        public string Name => "lua";

        /// <summary>Calls one workload once and returns its numeric result.</summary>
        public double Call(Workload workload, long size)
            => _script.Call(_functions[workload.Name], (int)size).Number;
    }
}
