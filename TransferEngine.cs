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
    }
    public static async Task<TransferHeader> ReceiveAsync(TcpListener listener, string folder, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken); await using var stream = client.GetStream(); var header = await ReadHeader(stream, cancellationToken); var safeName = Path.GetFileName(header.Name); if (string.IsNullOrWhiteSpace(safeName) || safeName != header.Name) throw new InvalidDataException("The incoming filename is unsafe."); Directory.CreateDirectory(folder); var target = Path.Combine(folder, safeName); await using (var file = File.Create(target)) { await CopyAsync(stream, file, header.Length, progress, cancellationToken); await file.FlushAsync(cancellationToken); } if (!Hash(target).Equals(header.Sha256, StringComparison.OrdinalIgnoreCase)) { File.Delete(target); throw new InvalidDataException("Transfer integrity verification failed."); } return header;
    }
    private static async Task WriteHeader(Stream stream, TransferHeader header, CancellationToken token) { var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(header)); await stream.WriteAsync(BitConverter.GetBytes(bytes.Length), token); await stream.WriteAsync(bytes, token); }
    private static async Task<TransferHeader> ReadHeader(Stream stream, CancellationToken token) { var length = new byte[4]; await ReadExact(stream, length, token); var bytes = new byte[BitConverter.ToInt32(length)]; await ReadExact(stream, bytes, token); return JsonSerializer.Deserialize<TransferHeader>(bytes) ?? throw new InvalidDataException("Invalid transfer header."); }
    private static async Task CopyAsync(Stream source, Stream target, long length, IProgress<double>? progress, CancellationToken token) { var buffer = new byte[128 * 1024]; long copied = 0; int read; while (copied < length && (read = await source.ReadAsync(buffer, token)) > 0) { await target.WriteAsync(buffer.AsMemory(0, read), token); copied += read; progress?.Report((double)copied / length); } if (copied != length) throw new EndOfStreamException("Transfer ended before all bytes arrived."); }
    private static async Task ReadExact(Stream stream, byte[] buffer, CancellationToken token) { var offset = 0; while (offset < buffer.Length) { var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), token); if (read == 0) throw new EndOfStreamException(); offset += read; } }
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
}
