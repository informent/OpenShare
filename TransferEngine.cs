using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace OpenShare;
public sealed record TransferHeader(string Name, long Length, string Sha256);
public sealed record TransferReceipt(string Direction, DateTimeOffset CompletedAt, string Name, long Length, string Sha256);
public sealed record PairingCode(string Host, int Port, string Fingerprint, string Secret)
{
    public override string ToString() => $"openshare1|{Host}|{Port}|{Fingerprint}|{Secret}";
    public static PairingCode Parse(string text)
    {
        var parts = text.Trim().Split('|');
        if (parts.Length != 5 || parts[0] != "openshare1" ||
            !IPAddress.TryParse(parts[1], out var address) || address.AddressFamily != AddressFamily.InterNetwork ||
            !int.TryParse(parts[2], out var port) || port is < 1 or > 65535 ||
            parts[3].Length != 64 || !parts[3].All(Uri.IsHexDigit) ||
            parts[4].Length != 64 || !parts[4].All(Uri.IsHexDigit))
            throw new FormatException("Paste one complete pairing code from the receiver.");
        return new(address.ToString(), port, parts[3], parts[4]);
    }
}
public sealed class ReceiveSession : IDisposable
{
    private readonly RSA key = RSA.Create(2048);
    public X509Certificate2 Certificate { get; }
    public byte[] Secret { get; } = RandomNumberGenerator.GetBytes(32);
    public ReceiveSession()
    {
        var request = new CertificateRequest("CN=OpenShare", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        // Schannel requires a key container, not an ephemeral CNG key. DefaultKeySet
        // creates a temporary container whose lifetime is owned by this certificate.
        var pfx = generated.Export(X509ContentType.Pfx);
        try { Certificate = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.DefaultKeySet); }
        finally { CryptographicOperations.ZeroMemory(pfx); }
    }
    public PairingCode Code(string host, int port) => new(host, port, Certificate.GetCertHashString(HashAlgorithmName.SHA256), Convert.ToHexString(Secret));
    public void Dispose() { Certificate.Dispose(); key.Dispose(); CryptographicOperations.ZeroMemory(Secret); }
}
public static class TransferEngine
{
    public static async Task<TransferReceipt> SendAsync(string path, PairingCode pairing, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        pairing = PairingCode.Parse(pairing.ToString());
        await using var file = File.OpenRead(path);
        // Hash before connecting so a large source cannot consume the receiver's idle timeout.
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken));
        file.Position = 0;
        using var client = new TcpClient();
        using var setupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        setupTimeout.CancelAfter(TimeSpan.FromSeconds(15));
        await client.ConnectAsync(pairing.Host, pairing.Port, setupTimeout.Token);
        await using var stream = new SslStream(client.GetStream(), false, (_, certificate, _, _) =>
            certificate is not null && CryptographicOperations.FixedTimeEquals(certificate.GetCertHash(HashAlgorithmName.SHA256), Convert.FromHexString(pairing.Fingerprint)));
        await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "OpenShare", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, setupTimeout.Token);
        await stream.WriteAsync(Convert.FromHexString(pairing.Secret), setupTimeout.Token);
        var accepted = new byte[1];
        await ReadExact(stream, accepted, setupTimeout.Token);
        if (accepted[0] != 1) throw new AuthenticationException("The receiver rejected this pairing code.");
        var header = new TransferHeader(Path.GetFileName(path), file.Length, hash);
        await WriteHeader(stream, header, cancellationToken);
        await CopyAsync(file, stream, file.Length, progress, cancellationToken);
        using var acknowledgementTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        acknowledgementTimeout.CancelAfter(TimeSpan.FromSeconds(30));
        var acknowledgement = new byte[1];
        await ReadExact(stream, acknowledgement, acknowledgementTimeout.Token);
        if (acknowledgement[0] != 1) throw new IOException("Receiver did not confirm verification.");
        return new("Sent", DateTimeOffset.UtcNow, header.Name, header.Length, header.Sha256);
    }
    public static async Task<TransferReceipt> ReceiveAsync(TcpListener listener, string folder, ReceiveSession session, Func<TransferHeader, CancellationToken, Task<bool>> approve, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = new SslStream(client.GetStream(), false);
        using var setupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        setupTimeout.CancelAfter(TimeSpan.FromSeconds(15));
        await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = session.Certificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, setupTimeout.Token);
        var secret = new byte[32];
        await ReadExact(stream, secret, setupTimeout.Token);
        if (!CryptographicOperations.FixedTimeEquals(secret, session.Secret)) throw new AuthenticationException("The sender used an invalid pairing code.");
        await stream.WriteAsync(new byte[] { 1 }, setupTimeout.Token);
        var header = await ReadHeader(stream, cancellationToken);
        var safeName = Path.GetFileName(header.Name);
        if (string.IsNullOrWhiteSpace(safeName) || safeName != header.Name || safeName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || safeName.EndsWith('.') || safeName.EndsWith(' ') || safeName is "." or "..") throw new InvalidDataException("The incoming filename is unsafe.");
        if (header.Length < 0 || header.Sha256 is null || header.Sha256.Length != 64 || !header.Sha256.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid transfer metadata.");
        var deviceName = safeName.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        if (deviceName is "CON" or "PRN" or "AUX" or "NUL" ||
            (deviceName.Length == 4 && (deviceName.StartsWith("COM") || deviceName.StartsWith("LPT")) && "123456789\u00b9\u00b2\u00b3".Contains(deviceName[3])))
            throw new InvalidDataException("Windows device names cannot be received as files.");
        var target = Path.Combine(folder, safeName);
        if (File.Exists(target) || Directory.Exists(target)) throw new IOException("A file with this name already exists. Nothing was overwritten.");
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!);
        const long reserve = 16 * 1024 * 1024;
        if (header.Length > Math.Max(0, drive.AvailableFreeSpace - reserve)) throw new IOException("Not enough free disk space to receive this file safely.");
        using var approvalTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        approvalTimeout.CancelAfter(TimeSpan.FromSeconds(20));
        if (!await approve(header, approvalTimeout.Token).WaitAsync(approvalTimeout.Token)) throw new IOException("The incoming file was declined.");
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(folder);
        var temporary = Path.Combine(folder, ".openshare-" + Guid.NewGuid().ToString("N") + ".partial");
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await CopyAsync(stream, file, header.Length, progress, cancellationToken);
                await file.FlushAsync(cancellationToken);
            }
            await using (var verification = File.OpenRead(temporary))
            {
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(verification, cancellationToken));
                if (!actual.Equals(header.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Transfer integrity verification failed.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, target, false);
            await stream.WriteAsync(new byte[] { 1 }, cancellationToken);
            return new("Received", DateTimeOffset.UtcNow, header.Name, header.Length, header.Sha256);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static async Task WriteHeader(Stream stream, TransferHeader header, CancellationToken token) { var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(header)); await stream.WriteAsync(BitConverter.GetBytes(bytes.Length), token); await stream.WriteAsync(bytes, token); }
    private static async Task<TransferHeader> ReadHeader(Stream stream, CancellationToken token) { var length = new byte[4]; await ReadExact(stream, length, token); var count = BitConverter.ToInt32(length); if (count < 1 || count > 16384) throw new InvalidDataException("Transfer header exceeds the allowed size."); var bytes = new byte[count]; await ReadExact(stream, bytes, token); return JsonSerializer.Deserialize<TransferHeader>(bytes) ?? throw new InvalidDataException("Invalid transfer header."); }
    private static async Task CopyAsync(Stream source, Stream target, long length, IProgress<double>? progress, CancellationToken token)
    {
        var buffer = new byte[128 * 1024]; long copied = 0;
        while (copied < length)
        {
            var read = await ReadWithTimeout(source, buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - copied)), token);
            if (read == 0) throw new EndOfStreamException("Transfer ended before all bytes arrived.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await target.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
            copied += read; progress?.Report((double)copied / length);
        }
        progress?.Report(1);
    }
    private static async Task<int> ReadWithTimeout(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try { return await stream.ReadAsync(buffer, timeout.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new IOException("The connection was idle for 30 seconds."); }
    }
    private static async Task ReadExact(Stream stream, byte[] buffer, CancellationToken token) { var offset = 0; while (offset < buffer.Length) { var read = await ReadWithTimeout(stream, buffer.AsMemory(offset, buffer.Length - offset), token); if (read == 0) throw new EndOfStreamException(); offset += read; } }
}
