using System.Security.Cryptography;
using AiUsage.Data;

namespace AiUsage;

/// <summary>
/// The key the credentials file is sealed with: 32 random bytes in a file of
/// its own beside it, readable by this user only, made on first use. It keeps a
/// copy of the credentials file alone from giving anything away and stops
/// casual reading; anyone who can read this user's files can read both. The
/// desktop's keyring would be the better home for it, and is still to come.
/// </summary>
public static class SecretKey
{
    private static string KeyPath => Path.Combine(AppPaths.Root, "key");

    public static ISecretSealer Sealer() => new AesSealer(LoadOrCreate());

    /// <summary>The app's folder, readable by this user only.</summary>
    public static void EnsureFolder()
    {
        AppPaths.Ensure();
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(AppPaths.Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static byte[] LoadOrCreate()
    {
        EnsureFolder();
        if (File.Exists(KeyPath))
        {
            var stored = Convert.FromBase64String(File.ReadAllText(KeyPath).Trim());
            if (stored.Length == 32) return stored;
            throw new InvalidDataException($"{KeyPath} is not a key: delete it and enter the server's token again");
        }

        var key = RandomNumberGenerator.GetBytes(32);
        var temp = KeyPath + ".tmp";
        // Made private before anything is in it, then moved into place, so the
        // key is never readable by anyone else, not even for a moment.
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var file = new FileStream(temp, options))
        using (var writer = new StreamWriter(file))
        {
            writer.Write(Convert.ToBase64String(key));
        }
        File.Move(temp, KeyPath, overwrite: false);
        return key;
    }
}
