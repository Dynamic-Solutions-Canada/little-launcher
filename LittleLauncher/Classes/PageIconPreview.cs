namespace LittleLauncher.Classes;

/// <summary>A decoded pending automatic icon, tied to the exact address it was obtained for.</summary>
internal sealed record PageIconPreview(string Url, byte[]? Png)
{
    /// <summary>
    /// Commits the exact preview to the managed automatic-icon path after the caller validates
    /// its source address/type. An explicit choice may replace a larger saved image. With no
    /// preview bytes, retains an existing file or returns an empty path for future page adoption.
    /// The atomic replacement prevents readers from observing a partially written image.
    /// </summary>
    internal string Store(string path)
    {
        if (Png is { Length: > 0 } bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temp, bytes);
                File.Move(temp, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
        }

        return File.Exists(path) ? path : "";
    }
}
