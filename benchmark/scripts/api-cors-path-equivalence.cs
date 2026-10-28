using System.Globalization;

namespace CorsPathEquivalence
{
    /// <summary>
    /// Diagnostic for benchmark/scripts/api-cors-span-20261028.sh: compares main's string.Split data-path predicate with the
    /// span matcher in src/TerraBase/Hosting/ClientCors.cs (both copied verbatim, on strings) over seeded random paths built
    /// from route words, lookalikes, case variants, empty segments and separators. Exits 1 on the first disagreement.
    /// </summary>
    internal static class Program
    {
        private static readonly string[] Words =
        [
            "", "", "v1", "V1", "v2", "v1x", "projects", "PROJECTS", "project", "projectsx", "databases", "Databases", "database",
            "collections", "COLLECTIONS", "transactions", "Transactions", "changes", "ChAnGeS", "changesx", "change", "p", "d", "sql",
            "a%2Fb", " ", "documents", "by-key",
        ];

        private static readonly string?[] Fixed = [null, "", "/", "//////", "///////", "/v1/projects/p/databases/d/changes"];

        private static int Main()
        {
            var random = new Random(20261028);
            long matched = 0;
            const int Cases = 5_000_000;
            for (var i = 0; i < Cases; i++)
            {
                var path = i % 4 == 0 ? Chars(random) : Route(random);
                var before = Before(path);
                if (before != After(path))
                {
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"DISAGREE before={before} path={path}"));
                    return 1;
                }

                matched += before ? 1 : 0;
            }

            foreach (var path in Fixed)
            {
                if (Before(path) != After(path))
                {
                    Console.WriteLine("DISAGREE on a fixed path");
                    return 1;
                }
            }

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"equivalent cases={Cases} matched={matched} fixed=6"));
            return 0;
        }

        private static string Route(Random random)
        {
            var count = random.Next(1, 12);
            var segments = new string[count];
            for (var i = 0; i < count; i++)
            {
                // Mostly the real prefix shape, so matches and near misses are both common.
                segments[i] = random.Next(3) == 0 ? Words[random.Next(Words.Length)] : i switch
                {
                    0 => "",
                    1 => "v1",
                    2 => "projects",
                    4 => "databases",
                    6 => Words[13 + random.Next(7)],
                    _ => "x",
                };
            }

            return string.Join('/', segments);
        }

        private static string Chars(Random random)
        {
            const string Alphabet = "//////v1pPcd";
            return string.Create(random.Next(0, 24), random, (span, state) =>
            {
                for (var i = 0; i < span.Length; i++)
                {
                    span[i] = Alphabet[state.Next(Alphabet.Length)];
                }
            });
        }

        private static bool Before(string? value)
        {
            var segments = value?.Split('/');
            return segments is { Length: >= 7 } && segments[0].Length == 0
                && segments[3].Length > 0 && segments[5].Length > 0
                && string.Equals(segments[1], "v1", StringComparison.OrdinalIgnoreCase)
                && string.Equals(segments[2], "projects", StringComparison.OrdinalIgnoreCase)
                && string.Equals(segments[4], "databases", StringComparison.OrdinalIgnoreCase)
                && (string.Equals(segments[6], "collections", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(segments[6], "transactions", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(segments[6], "changes", StringComparison.OrdinalIgnoreCase));
        }

        private static bool After(string? path)
        {
            var value = path.AsSpan();
            Span<Range> segments = stackalloc Range[8];
            return value.Split(segments, '/') >= 7 && value[segments[0]].IsEmpty
                && !value[segments[3]].IsEmpty && !value[segments[5]].IsEmpty
                && value[segments[1]].Equals("v1", StringComparison.OrdinalIgnoreCase)
                && value[segments[2]].Equals("projects", StringComparison.OrdinalIgnoreCase)
                && value[segments[4]].Equals("databases", StringComparison.OrdinalIgnoreCase)
                && (value[segments[6]].Equals("collections", StringComparison.OrdinalIgnoreCase)
                    || value[segments[6]].Equals("transactions", StringComparison.OrdinalIgnoreCase)
                    || value[segments[6]].Equals("changes", StringComparison.OrdinalIgnoreCase));
        }
    }
}
