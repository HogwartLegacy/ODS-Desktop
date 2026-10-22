namespace Ods.Core.Platform;

/// <summary>Path rules for handing files to llama.cpp on Windows.</summary>
public static class WindowsPaths
{
    /// <summary>
    /// A path llama-server.exe can open. It reads argv in the ANSI code page
    /// but opens files as UTF-8 (ggml_fopen), so a non-ASCII path (for example
    /// a user folder named José) fails; its 8.3 short form is used instead.
    /// Mirrors installers/windows/lib/native-llama-runtime.ps1.
    /// </summary>
    public static string ToLlamaArgumentPath(string path)
    {
        if (IsPrintableAscii(path)) return path;
        var shortPath = ShortPath(path);
        if (shortPath is null)
        {
            var parent = Path.GetDirectoryName(path);
            var leaf = Path.GetFileName(path);
            var shortParent = parent is null ? null : ShortPath(parent);
            if (shortParent is not null && IsPrintableAscii(leaf)) shortPath = Path.Combine(shortParent, leaf);
        }
        if (shortPath is not null && IsPrintableAscii(shortPath)) return shortPath;
        throw new InvalidOperationException(
            $"llama.cpp on Windows cannot open '{path}': the path has non-ASCII characters and no 8.3 short name. "
            + "Choose a models folder whose path uses only English letters and digits (Settings > Storage), or enable 8.3 names on that drive.");
    }

    public static bool IsPrintableAscii(string value) => value.All(c => c is >= ' ' and <= '~');

    private static unsafe string? ShortPath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return null;
        var buffer = new char[32768];
        fixed (char* pointer = buffer)
        {
            var length = NativeMethods.GetShortPathName(path, pointer, (uint)buffer.Length);
            if (length == 0 || length >= buffer.Length) return null;
            return new string(buffer, 0, (int)length);
        }
    }
}
