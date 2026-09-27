using System.Text.Json;

namespace Rtfc.Core;

/// <summary>
/// <c>status.json</c> (spec §11): written by the daemon whenever the inbox changes, read
/// by <c>rtfc statusline</c> every few seconds. Written atomically, temp file then
/// rename, so the reader never sees half a file.
/// </summary>
public static class StatusFile
{
    public static void Write(string path, StatusSnapshot snapshot)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(snapshot, CoreJson.Default.StatusSnapshot));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Null when there is no file or it cannot be read, which the status line shows as nothing.</summary>
    public static StatusSnapshot? Read(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonSerializer.Deserialize(File.ReadAllBytes(path), CoreJson.Default.StatusSnapshot);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
