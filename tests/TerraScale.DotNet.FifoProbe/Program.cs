using TerraScale.DotNet.Native;

if (args.Length != 1) return 2;
Console.WriteLine("FIFO_PROBE_READY");
try
{
    await NativeOperationStore.LoadAsync(args[0]);
    Console.Error.WriteLine("A FIFO was accepted as a retained operation.");
    return 1;
}
catch (InvalidDataException)
{
    return 0;
}
