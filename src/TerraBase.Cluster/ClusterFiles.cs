namespace TerraBase.Cluster;

internal static class ClusterFiles
{
    public static Result<byte[], Validation> ReadBytes(string path)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            return Unreadable(path);
        }
        catch (UnauthorizedAccessException)
        {
            return Unreadable(path);
        }
    }

    private static Validation Unreadable(string path) =>
        new(string.Concat("cluster file '", Path.GetFileName(path), "' could not be read"), "cluster_file_unreadable");
}
