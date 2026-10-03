using System.Text.Json;
using System.IO;

namespace OpenShare;

public static class TransferHistory
{
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenShare", "transfer-history.json");

    public static IReadOnlyList<TransferReceipt> Load(string? path = null)
    {
        path ??= DefaultPath;
        try { return File.Exists(path) ? JsonSerializer.Deserialize<List<TransferReceipt>>(File.ReadAllText(path)) ?? [] : []; }
        catch { return []; }
    }

    public static void Append(TransferReceipt receipt, string? path = null, int limit = 100)
    {
        path ??= DefaultPath;
        var directory = Path.GetDirectoryName(path)!; Directory.CreateDirectory(directory);
        var entries = Load(path).Append(receipt).TakeLast(Math.Max(1, limit)).ToArray();
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
