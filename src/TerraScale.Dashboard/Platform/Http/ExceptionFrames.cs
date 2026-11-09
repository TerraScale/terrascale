using System.Diagnostics;
using System.Globalization;

namespace TerraScale.Dashboard.Platform.Http
{
    public static class ExceptionFrames
    {
        private const int MaxFrames = 12;
        private const int MaxInner = 3;

        public static string Describe(Exception error)
        {
            var parts = new List<string>();
            for (var current = error; current is not null && parts.Count < MaxInner; current = current.InnerException)
            {
                var frames = new StackTrace(current, true).GetFrames().Select(Frame).OfType<string>().Take(MaxFrames).ToArray();
                parts.Add(current.GetType().Name + (frames.Length == 0 ? "" : " at " + string.Join(" < ", frames)));
            }

            return string.Join(" | caused by ", parts);
        }

        private static string? Frame(StackFrame frame)
        {
            if (frame.GetMethod() is not { DeclaringType: { } type } method)
            {
                return null;
            }

            var name = type.Name.StartsWith('<') && type.DeclaringType is { } owner
                ? owner.Name + "." + type.Name[1..type.Name.IndexOf('>', StringComparison.Ordinal)]
                : type.Name + "." + method.Name;
            var line = frame.GetFileLineNumber();
            return line > 0 ? name + ":" + line.ToString(CultureInfo.InvariantCulture) : name;
        }
    }
}
