namespace TerraBase.Engine;

internal sealed class DatabaseFencedException() : Exception(Refusal)
{
    internal const string Refusal = "the database refuses reads and writes until its region writer proves its data holds every acknowledged write; retry later";
}
