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
    internal sealed class LuaDriver
    {
        private readonly Script _script;
        private readonly Dictionary<string, DynValue> _functions = new();

        public static LuaDriver Load(string source)
        {
            // The workloads use only plain arithmetic, pcall/error, and ipairs, so the module set
            // stays deliberately small instead of taking the whole preset.
            var script = new Script(CoreModules.Basic | CoreModules.TableIterators | CoreModules.Math | CoreModules.ErrorHandling);
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

        /// <summary>Calls one workload once and returns its numeric result.</summary>
        public double CallNumber(string function, long size)
            => _script.Call(_functions[function], (int)size).Number;
    }
}
