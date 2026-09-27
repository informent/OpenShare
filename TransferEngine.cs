using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace OpenShare;
public sealed record TransferHeader(string Name, long Length, string Sha256);
public static class TransferEngine
{
    public static async Task SendAsync(string path, string host, int port, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        await using var file = File.OpenRead(path); using var client = new TcpClient(); await client.ConnectAsync(host, port, cancellationToken); await using var stream = client.GetStream(); var header = new TransferHeader(Path.GetFileName(path), file.Length, Hash(path)); await WriteHeader(stream, header, cancellationToken); await CopyAsync(file, stream, file.Length, progress, cancellationToken);
        using var acknowledgementTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        acknowledgementTimeout.CancelAfter(TimeSpan.FromSeconds(30));
        var acknowledgement = new byte[1];
        await ReadExact(stream, acknowledgement, acknowledgementTimeout.Token);
        if (acknowledgement[0] != 1) throw new IOException("Receiver did not confirm verification.");
    }
    public static async Task<TransferHeader> ReceiveAsync(TcpListener listener, string folder, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = client.GetStream();
        var header = await ReadHeader(stream, cancellationToken);
        var safeName = Path.GetFileName(header.Name);
        if (string.IsNullOrWhiteSpace(safeName) || safeName != header.Name || safeName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || safeName.EndsWith('.') || safeName.EndsWith(' ') || safeName is "." or "..") throw new InvalidDataException("The incoming filename is unsafe.");
        if (header.Length < 0 || header.Sha256 is null || header.Sha256.Length != 64 || !header.Sha256.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid transfer metadata.");
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, safeName);
        if (File.Exists(target) || Directory.Exists(target)) throw new IOException("A file with this name already exists. Nothing was overwritten.");
        var temporary = Path.Combine(folder, ".openshare-" + Guid.NewGuid().ToString("N") + ".partial");
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await CopyAsync(stream, file, header.Length, progress, cancellationToken);
                await file.FlushAsync(cancellationToken);
            }
            if (!Hash(temporary).Equals(header.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Transfer integrity verification failed.");
            File.Move(temporary, target, false);
            await stream.WriteAsync(new byte[] { 1 }, cancellationToken);
            return header;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static async Task WriteHeader(Stream stream, TransferHeader header, CancellationToken token) { var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(header)); await stream.WriteAsync(BitConverter.GetBytes(bytes.Length), token); await stream.WriteAsync(bytes, token); }
    private static async Task<TransferHeader> ReadHeader(Stream stream, CancellationToken token) { var length = new byte[4]; await ReadExact(stream, length, token); var count = BitConverter.ToInt32(length); if (count < 1 || count > 16384) throw new InvalidDataException("Transfer header exceeds the allowed size."); var bytes = new byte[count]; await ReadExact(stream, bytes, token); return JsonSerializer.Deserialize<TransferHeader>(bytes) ?? throw new InvalidDataException("Invalid transfer header."); }
    private static async Task CopyAsync(Stream source, Stream target, long length, IProgress<double>? progress, CancellationToken token) { var buffer = new byte[128 * 1024]; long copied = 0; int read; while (copied < length && (read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - copied)), token)) > 0) { await target.WriteAsync(buffer.AsMemory(0, read), token); copied += read; progress?.Report((double)copied / length); } if (copied != length) throw new EndOfStreamException("Transfer ended before all bytes arrived."); }
    private static async Task ReadExact(Stream stream, byte[] buffer, CancellationToken token) { var offset = 0; while (offset < buffer.Length) { var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), token); if (read == 0) throw new EndOfStreamException(); offset += read; } }
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
}
