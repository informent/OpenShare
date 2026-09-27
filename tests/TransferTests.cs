using System.Net;
using System.Net.Sockets;
using OpenShare;
using System.Text.Json;
var root = Path.Combine(Path.GetTempPath(), "openshare-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    foreach (var size in new[] { 0, 100_000, 2_000_000 })
    {
        var source = Path.Combine(root, $"payload-{size}.bin");
        File.WriteAllBytes(source, Enumerable.Range(0, size).Select(i => (byte)(i % 251)).ToArray());
        var destination = Path.Combine(root, "received");
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var receive = TransferEngine.ReceiveAsync(listener, destination, cancellationToken: timeout.Token);
        await TransferEngine.SendAsync(source, "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, cancellationToken: timeout.Token);
        var header = await receive;
        if (!File.ReadAllBytes(Path.Combine(destination, header.Name)).SequenceEqual(File.ReadAllBytes(source))) throw new Exception("Content mismatch.");
    }
    await Reject("collision", new TransferHeader("existing.txt", 0, new string('0', 64)), Array.Empty<byte>(), true);
    await Reject("traversal", new TransferHeader("../outside.txt", 0, new string('0', 64)), Array.Empty<byte>());
    await Reject("negative", new TransferHeader("file.txt", -1, new string('0', 64)), Array.Empty<byte>());
    await Reject("tamper", new TransferHeader("file.txt", 3, new string('0', 64)), new byte[] { 1, 2, 3 });
    await Reject("truncated", new TransferHeader("file.txt", 8, new string('0', 64)), new byte[] { 1 });
    await Reject("oversized-header", null, Array.Empty<byte>());
    Console.WriteLine("PASS: 3 verified round trips and 6 rejected unsafe transfers; existing files preserved and partial files removed.");
}
finally { Directory.Delete(root, true); }

async Task Reject(string name, TransferHeader? header, byte[] payload, bool existing = false)
{
    var destination = Path.Combine(root, name); Directory.CreateDirectory(destination);
    if (existing) File.WriteAllText(Path.Combine(destination, "existing.txt"), "keep me");
    using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var receiving = TransferEngine.ReceiveAsync(listener, destination, cancellationToken: timeout.Token);
    using (var client = new TcpClient())
    {
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        var stream = client.GetStream();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(header);
        await stream.WriteAsync(BitConverter.GetBytes(header is null ? int.MaxValue : bytes.Length));
        if (header is not null) { await stream.WriteAsync(bytes); await stream.WriteAsync(payload); }
        client.Client.Shutdown(SocketShutdown.Send);
        try { await receiving; throw new Exception("Unsafe transfer accepted: " + name); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException) { }
    }
    if (Directory.GetFiles(destination).Length != (existing ? 1 : 0)) throw new Exception("Partial file remains: " + name);
    if (existing && File.ReadAllText(Path.Combine(destination, "existing.txt")) != "keep me") throw new Exception("Existing file changed.");
}
