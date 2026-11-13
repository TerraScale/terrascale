using System.Text;
using TerraBase.Engine.Turso;
using TerraBase.Primitives;

namespace TerraBase.Configuration;

internal static class KeyringHistory
{
    internal static void Record(string directory, IReadOnlyCollection<string> keys)
    {
        try
        {
            Directory.CreateDirectory(directory);
            DatabaseKeys.RejectLink(directory);
            var path = Path.Combine(directory, ".master-key-history");
            DatabaseKeys.RejectLink(path);
            FileStream file;
            var created = false;
            try
            {
                file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (FileNotFoundException)
            {
                file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                created = true;
            }
            using (file)
            {
                if (file.Length > 4096 || !created && file.Length == 0)
                {
                    throw new InvalidDataException("the configured key history is malformed");
                }
                var bytes = new byte[checked((int)file.Length)];
                file.ReadExactly(bytes);
                var text = Encoding.ASCII.GetString(bytes);
                var recorded = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                if (text.Length > 0 && !text.EndsWith('\n') || recorded.Any(id => !KeyringSettings.ValidId(id))
                    || recorded.Distinct(StringComparer.Ordinal).Count() != recorded.Length)
                {
                    throw new InvalidDataException("the configured key history is malformed");
                }
                foreach (var id in recorded)
                {
                    if (!keys.Contains(id, StringComparer.Ordinal))
                    {
                        throw new TerraBaseConfigurationException($"configured master key id '{id}' cannot be removed");
                    }
                }
                foreach (var id in keys.Order(StringComparer.Ordinal).Except(recorded, StringComparer.Ordinal))
                {
                    file.Write(Encoding.ASCII.GetBytes(id + "\n"));
                }
                file.Flush(flushToDisk: true);
            }
            DirectorySync.Sync(directory);
            DirectorySync.Sync(Path.GetDirectoryName(directory)!);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new TerraBaseConfigurationException("configured master key history cannot be read or durably recorded");
        }
    }
}
