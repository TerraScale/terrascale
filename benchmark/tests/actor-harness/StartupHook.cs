using System.Diagnostics.Metrics;

// Fault injection in a child process: fail initialization or stall a real consumer.
internal static class StartupHook
{
    private static readonly MeterListener Listener = new();
    private static readonly ManualResetEventSlim NeverCompleted = new(false);

    public static void Initialize()
    {
        var initializationCleanup = Environment.GetEnvironmentVariable("ACTOR_CHECK_INITIALIZATION_CLEANUP");
        var initializationBlock = Environment.GetEnvironmentVariable("ACTOR_CHECK_INITIALIZATION_BLOCK");
        Listener.MeasurementsCompleted = (instrument, _) =>
        {
            if (initializationCleanup is not null && instrument.Name == "terrabase.actors.rejected")
            {
                File.WriteAllText(initializationCleanup, instrument.Name);
            }
        };
        Listener.InstrumentPublished = (instrument, listener) =>
        {
            if (initializationCleanup is not null)
            {
                if (instrument.Name == "terrabase.actors.rejected")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
                else if (instrument.Name == "terrabase.actors.completed")
                {
                    if (initializationBlock == "forever")
                    {
                        NeverCompleted.Wait();
                    }
                    else if (initializationBlock == "stdin")
                    {
                        _ = Console.ReadLine();
                    }
                    else
                    {
                        throw new InvalidOperationException("actor initialization fault");
                    }
                }
            }
            else if (instrument.Name == "terrabase.actors.completed")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        Listener.SetMeasurementEventCallback<long>((_, _, _, _) => NeverCompleted.Wait());
        Listener.Start();
    }
}
