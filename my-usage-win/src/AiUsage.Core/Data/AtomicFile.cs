namespace AiUsage.Data;

/// <summary>
/// Writes a file so that a crash or a power cut mid-write leaves the old contents
/// rather than half of the new ones. The credentials file is the reason: a
/// truncated one reads as "no tokens", which signs every provider out at once.
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string contents)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // Same folder as the target, so the move below is a rename on one volume
        // and never a copy across two.
        var temp = path + ".tmp";
        File.WriteAllText(temp, contents);
        File.Move(temp, path, overwrite: true);
    }
}
