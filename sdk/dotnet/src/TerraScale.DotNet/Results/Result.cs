namespace TerraScale.Results;

/// <summary>Represents a completed operation with no payload.</summary>
public sealed record Done;

/// <summary>A flat union representing either a success value or one failure case.</summary>
public readonly union Result<T, T1>(T, T1);

/// <summary>A flat union representing either a success value or one of two failure cases.</summary>
public readonly union Result<T, T1, T2>(T, T1, T2);

/// <summary>A flat union representing either a success value or one of three failure cases.</summary>
public readonly union Result<T, T1, T2, T3>(T, T1, T2, T3);

/// <summary>A flat union representing either a success value or one of four failure cases.</summary>
public readonly union Result<T, T1, T2, T3, T4>(T, T1, T2, T3, T4);

/// <summary>A flat union representing either a success value or one of five failure cases.</summary>
public readonly union Result<T, T1, T2, T3, T4, T5>(T, T1, T2, T3, T4, T5);

/// <summary>A flat union representing either a success value or one of six failure cases.</summary>
public readonly union Result<T, T1, T2, T3, T4, T5, T6>(T, T1, T2, T3, T4, T5, T6);
