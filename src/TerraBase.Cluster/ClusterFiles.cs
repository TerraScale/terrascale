namespace TerraBase.Cluster;

internal readonly union ClusterFileReadOutcome(byte[], ClusterConfigurationError);

internal static class ClusterFiles
{
    public static ClusterFileReadOutcome ReadBytes(string path)
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

    private static ClusterConfigurationError Unreadable(string path) =>
        new(string.Concat("cluster file '", Path.GetFileName(path), "' could not be read"), "cluster_file_unreadable");
}
