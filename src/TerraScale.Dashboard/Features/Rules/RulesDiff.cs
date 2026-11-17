namespace TerraScale.Dashboard.Features.Rules
{
    public enum RulesDiffKind
    {
        Same,
        Removed,
        Added,
    }

    public sealed record RulesDiffLine(RulesDiffKind Kind, string Text);

    public static class RulesDiff
    {
        private const long MaxCells = 4_000_000;

        public static IReadOnlyList<RulesDiffLine> Lines(string before, string after)
        {
            var old = before.Split('\n');
            var current = after.Split('\n');
            var start = 0;
            while (start < old.Length && start < current.Length && old[start] == current[start])
            {
                start++;
            }

            var oldEnd = old.Length;
            var currentEnd = current.Length;
            while (oldEnd > start && currentEnd > start && old[oldEnd - 1] == current[currentEnd - 1])
            {
                oldEnd--;
                currentEnd--;
            }

            List<RulesDiffLine> lines = [.. old[..start].Select(line => new RulesDiffLine(RulesDiffKind.Same, line))];
            lines.AddRange(Middle(old[start..oldEnd], current[start..currentEnd]));
            lines.AddRange(old[oldEnd..].Select(line => new RulesDiffLine(RulesDiffKind.Same, line)));
            return lines;
        }

        private static List<RulesDiffLine> Middle(string[] old, string[] current)
        {
            if ((long)old.Length * current.Length > MaxCells)
            {
                return [.. old.Select(line => new RulesDiffLine(RulesDiffKind.Removed, line)), .. current.Select(line => new RulesDiffLine(RulesDiffKind.Added, line))];
            }

            var width = current.Length + 1;
            var common = new int[(old.Length + 1) * width];
            for (var i = old.Length - 1; i >= 0; i--)
            {
                for (var j = current.Length - 1; j >= 0; j--)
                {
                    common[(i * width) + j] = old[i] == current[j] ? common[((i + 1) * width) + j + 1] + 1 : Math.Max(common[((i + 1) * width) + j], common[(i * width) + j + 1]);
                }
            }

            List<RulesDiffLine> lines = [];
            int x = 0, y = 0;
            while (x < old.Length || y < current.Length)
            {
                if (x < old.Length && y < current.Length && old[x] == current[y])
                {
                    lines.Add(new(RulesDiffKind.Same, old[x]));
                    x++;
                    y++;
                }
                else if (x < old.Length && (y == current.Length || common[((x + 1) * width) + y] >= common[(x * width) + y + 1]))
                {
                    lines.Add(new(RulesDiffKind.Removed, old[x]));
                    x++;
                }
                else
                {
                    lines.Add(new(RulesDiffKind.Added, current[y]));
                    y++;
                }
            }

            return lines;
        }
    }
}
