namespace TerraBase.Engine;

/// <summary>A committed transaction's position: the database's commit offset after it.</summary>
public readonly record struct CommitResult(long Offset);
