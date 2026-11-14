using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Turso.Raw.Public;

public sealed class StartupHook
{
    public static void Initialize()
    {
        if (Environment.GetEnvironmentVariable("ENGINE_INTEGRITY_PROXY") is not { } proxy)
        {
            return;
        }

        NativeLibrary.SetDllImportResolver(typeof(TursoBindings).Assembly, (name, assembly, path) =>
            name == "turso_sdk_kit" ? NativeLibrary.Load(proxy) : IntPtr.Zero);
    }
}
