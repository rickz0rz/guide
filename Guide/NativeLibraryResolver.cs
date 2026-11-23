using System.Reflection;
using System.Runtime.InteropServices;

namespace Guide;

// Homebrew installs SDL3 in /opt/homebrew/lib (Apple silicon) or /usr/local/lib (Intel).
// .NET does not look in these folders, so look there when the usual search fails.
public static class NativeLibraryResolver
{
    private static readonly string[] MacSearchFolders = ["/opt/homebrew/lib", "/usr/local/lib"];

    public static void Register()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        NativeLibrary.SetDllImportResolver(typeof(SDL3.SDL).Assembly, Resolve);
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (NativeLibrary.TryLoad(libraryName, assembly, searchPath, out var handle))
            return handle;

        var fileName = libraryName.StartsWith("lib") ? $"{libraryName}.dylib" : $"lib{libraryName}.dylib";

        foreach (var folder in MacSearchFolders)
        {
            if (NativeLibrary.TryLoad(Path.Combine(folder, fileName), out handle))
                return handle;
        }

        return IntPtr.Zero;
    }
}
