using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Collections.Generic;

namespace WooldrumsModestMenu;

// Loads missing interop assemblies from BepInEx/interop before Plugin.Load runs.
internal static class InteropAssemblyResolver
{
    private static bool _installed;
    private static string? _interopDir;

    [ModuleInitializer]
    internal static void Install()
    {
        if (_installed)
            return;
        _installed = true;

        try
        {
            var pluginDir = Path.GetDirectoryName(typeof(InteropAssemblyResolver).Assembly.Location);
            if (string.IsNullOrEmpty(pluginDir))
                return;

            var interop = Path.GetFullPath(Path.Combine(pluginDir, "..", "interop"));
            if (!Directory.Exists(interop))
                return;

            _interopDir = interop;
            AppDomain.CurrentDomain.AssemblyResolve += ResolveFromInterop;
        }
        catch
        {
            // best effort; fall back to BepInEx's own resolution
        }
    }

    private static readonly Dictionary<string, Assembly?> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static Assembly? ResolveFromInterop(object? sender, ResolveEventArgs args)
    {
        if (_interopDir == null)
            return null;

        var simpleName = new AssemblyName(args.Name).Name;
        if (string.IsNullOrEmpty(simpleName))
            return null;

        if (Cache.TryGetValue(simpleName, out var cached))
            return cached;

        Assembly? result = null;
        try
        {
            var candidate = Path.Combine(_interopDir, simpleName + ".dll");
            if (File.Exists(candidate))
                result = Assembly.LoadFrom(candidate);
        }
        catch
        {
            result = null;
        }

        Cache[simpleName] = result;
        return result;
    }
}
