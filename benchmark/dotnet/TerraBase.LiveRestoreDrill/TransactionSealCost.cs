using System.Diagnostics;
using System.Security.Cryptography;
using TerraBase.Features.HomePush;
using TerraBase.Tenancy;

namespace TerraBase.LiveRestoreDrill;

internal static class TransactionSealCost
{
    internal static void Run()
    {
        var tenant = new TenantContext(Ulid.NewUlid().ToString(), PlanTier.Hobby, Ulid.NewUlid().ToString(), Ulid.NewUlid().ToString());
        var id = Ulid.NewUlid().ToString();
        var key = RandomNumberGenerator.GetBytes(32);
        try
        {
            Console.WriteLine("repeat,compressed_bytes,iterations,microseconds_per_seal,allocated_bytes_per_seal");
            foreach (var size in new[] { 256, 4096, 65536 })
            {
                var packed = new byte[size];
                for (var warm = 0; warm < 1000; warm++)
                {
                    _ = TransactionCrypto.Seal(tenant, packed, id, key);
                }
                for (var repeat = 1; repeat <= 3; repeat++)
                {
                    const int iterations = 10000;
                    var allocated = GC.GetAllocatedBytesForCurrentThread();
                    var started = Stopwatch.GetTimestamp();
                    for (var index = 0; index < iterations; index++)
                    {
                        _ = TransactionCrypto.Seal(tenant, packed, id, key);
                    }
                    var elapsed = Stopwatch.GetElapsedTime(started).TotalMicroseconds / iterations;
                    var bytes = (GC.GetAllocatedBytesForCurrentThread() - allocated) / iterations;
                    Console.WriteLine(FormattableString.Invariant($"{repeat},{size},{iterations},{elapsed:F3},{bytes}"));
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }
}
