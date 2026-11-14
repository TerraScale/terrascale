using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;

namespace Journey.Keyring;

internal static class PreviousBuild
{
    internal static async Task OpenAsync(string file)
    {
        using var input = JsonDocument.Parse(File.ReadAllText(file));
        var root = input.RootElement;
        var context = new AssemblyLoadContext("previous-keyring-reader", isCollectible: true);
        var master = Convert.FromBase64String(root.GetProperty("master").GetString()!);
        try
        {
            var assembly = context.LoadFromAssemblyPath(root.GetProperty("baseline").GetString()!);
            var type = assembly.GetType("TerraBase.Features.Backup.MasterKey", throwOnError: true)!;
            var reader = Activator.CreateInstance(type, [master]) ?? throw new InvalidOperationException("previous reader missing");
            var unwrap = type.GetMethod("Unwrap", BindingFlags.Instance | BindingFlags.Public)
                ?? throw new InvalidOperationException("previous unwrap missing");
            if (root.TryGetProperty("credential", out var credential))
            {
                var sealedType = assembly.GetType("TerraBase.Features.Backup.SealedCredentials", throwOnError: true)!;
                var sealedValue = Activator.CreateInstance(sealedType, [credential.GetProperty("WrappedKey").GetString(), credential.GetProperty("Ciphertext").GetString()]);
                var open = assembly.GetType("TerraBase.Features.Backup.CustomerCredentials", throwOnError: true)!
                    .GetMethod("OpenAsync", BindingFlags.Static | BindingFlags.Public)!;
                var task = (Task<(string AccessKeyId, string SecretKey)>)open.Invoke(null, [reader, sealedValue, CancellationToken.None])!;
                var result = await task;
                if (result.AccessKeyId != credential.GetProperty("AccessKeyId").GetString() || result.SecretKey != credential.GetProperty("SecretKey").GetString())
                {
                    throw new InvalidDataException("previous build did not open new credentials");
                }
                Console.WriteLine("keyring-driver:previous credential plaintext matched");
            }
            foreach (var wrapped in root.GetProperty("wrappers").EnumerateArray())
            {
                var key = (byte[])(unwrap.Invoke(reader, [wrapped.GetString()]) ?? throw new InvalidDataException("previous unwrap returned no key"));
                try
                {
                    if (key.Length != 32)
                    {
                        throw new InvalidDataException("previous build returned an invalid key");
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(key);
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(master);
            context.Unload();
        }
    }
}
