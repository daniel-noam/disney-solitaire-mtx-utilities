using System;
using System.Collections.Generic;
using SuperPlay.Domino.TemplatesBehavior.Runtime;
using SuperPlay.Domino.TemplatesBehavior.Runtime.Nodes;

namespace Utilities.Editor
{
    /// <summary>
    /// The renames one version of a node made to the last, where matching by name cannot see it.
    ///
    /// Most of an upgrade needs nothing here: a V2 usually keeps the port and field names and adds
    /// to them. This is for the ones that renamed something without changing what it means — and
    /// there is no way to work that out from the outside, because a renamed port and a removed one
    /// look identical. Somebody has to say which it was, so it is written down here rather than
    /// guessed at.
    ///
    /// An entry is a line. Adding one is how a node pair stops reporting a lost connection that is
    /// not really lost.
    /// </summary>
    internal static class NodeUpgradeAliases
    {
        private sealed class Renames
        {
            public readonly Dictionary<string, string> Ports = new Dictionary<string, string>();
            public readonly Dictionary<string, string> Fields = new Dictionary<string, string>();

            public Renames Port(string from, string to)
            {
                Ports[from] = to;
                return this;
            }

            public Renames Field(string from, string to)
            {
                Fields[from] = to;
                return this;
            }
        }

        private static readonly Dictionary<(Type From, Type To), Renames> Table =
            new Dictionary<(Type, Type), Renames>
            {
                // StringFormatV2 takes any number of arguments, so the one the old node called
                // Value became the first of many and was renamed Arg. Same value, same meaning.
                {
                    (typeof(StringFormat), typeof(StringFormatV2)),
                    new Renames()
                        .Port(PortNames.PORT_NAME_VALUE, PortNames.PORT_NAME_ARG)
                        .Field("_value", "_arg")
                },
            };

        /// <summary>The new node's name for this port, or the same name when nothing renamed it.</summary>
        public static string Port(Type from, Type to, string name) => Lookup(from, to, name, true);

        /// <summary>The new node's name for this field, or the same name when nothing renamed it.</summary>
        public static string Field(Type from, Type to, string name) => Lookup(from, to, name, false);

        private static string Lookup(Type from, Type to, string name, bool port)
        {
            if (from == null || to == null || name == null) return name;
            if (!Table.TryGetValue((from, to), out var renames)) return name;

            var map = port ? renames.Ports : renames.Fields;
            return map.TryGetValue(name, out var renamed) ? renamed : name;
        }
    }
}
