#nullable enable

using System.Text;

namespace Surtr.Interop.Attributes
{
    /// <summary>
    /// What a name being adapted denotes, so <see cref="SurtrNamingPolicy.Surtr"/> can treat types
    /// and members differently (Surtr types are PascalCase, members camelCase).
    /// </summary>
    public enum SurtrNameKind
    {
        /// <summary>A type (class, struct, enum or delegate) name.</summary>
        Type,

        /// <summary>A member (method, field, property, constructor or parameter) name.</summary>
        Member,
    }

    /// <summary>
    /// Applies a <see cref="SurtrNamingPolicy"/> to a CLR name, producing the Surtr name. Pure and
    /// dependency-free, so both the runtime bridge and the source generator can share the rule.
    /// </summary>
    public static class SurtrNaming
    {
        /// <summary>
        /// Adapts <paramref name="name"/> according to <paramref name="policy"/>, honouring the
        /// type/member split only for <see cref="SurtrNamingPolicy.Surtr"/>.
        /// </summary>
        public static string Apply(string name, SurtrNamingPolicy policy, SurtrNameKind kind)
        {
            if (string.IsNullOrEmpty(name))
                return name ?? string.Empty;

            if (policy == SurtrNamingPolicy.Default)
                policy = SurtrNamingPolicy.Surtr;

            switch (policy)
            {
                case SurtrNamingPolicy.Surtr:
                    return kind == SurtrNameKind.Member ? LowerFirst(name) : name;

                case SurtrNamingPolicy.PascalCase:
                    return name;

                case SurtrNamingPolicy.CamelCase:
                    return LowerFirst(name);

                case SurtrNamingPolicy.SnakeCase:
                    return ToSnakeCase(name);

                case SurtrNamingPolicy.LowerCase:
                    return name.ToLowerInvariant();

                case SurtrNamingPolicy.UpperCase:
                    return name.ToUpperInvariant();

                default:
                    return name;
            }
        }

        private static string LowerFirst(string name)
        {
            char first = name[0];
            char lowered = char.ToLowerInvariant(first);
            if (first == lowered)
                return name;

            return lowered + name.Substring(1);
        }

        private static string ToSnakeCase(string name)
        {
            var builder = new StringBuilder(name.Length + 4);

            for (int i = 0; i < name.Length; i++)
            {
                char current = name[i];

                if (char.IsUpper(current))
                {
                    bool previousIsLowerOrDigit =
                        i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]));
                    bool nextIsLower = i + 1 < name.Length && char.IsLower(name[i + 1]);

                    // An uppercase letter starts a new word when it follows a lowercase/digit
                    // (DoWork -> do_work) or when it is the last of an acronym run before a lower
                    // letter (HTTPResponse -> http_response).
                    if (builder.Length > 0
                        && builder[builder.Length - 1] != '_'
                        && (previousIsLowerOrDigit || nextIsLower))
                    {
                        builder.Append('_');
                    }

                    builder.Append(char.ToLowerInvariant(current));
                }
                else
                {
                    builder.Append(current);
                }
            }

            return builder.ToString();
        }
    }
}
