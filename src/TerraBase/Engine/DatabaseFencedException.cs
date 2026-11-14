namespace TerraBase.Engine;

internal sealed class DatabaseFencedException() : Exception("the database refuses writes until its region writer proves its data holds every acknowledged write; retry later");
