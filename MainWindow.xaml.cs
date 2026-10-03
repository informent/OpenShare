using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using Forms = System.Windows.Forms;
namespace OpenShare;
public partial class MainWindow : Window
{
    private TcpListener? listener; private CancellationTokenSource? cancellation; private CancellationTokenSource? sending; private string? receiveFolder; private string? selectedFile; private int port;
    private long transferSize;
    public MainWindow() { InitializeComponent(); Closed += (_, _) => { cancellation?.Cancel(); sending?.Cancel(); }; Opacity = 0; Loaded += (_, _) => BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240))); }
    private async void Listen_Click(object sender, RoutedEventArgs e)
    {
        if (cancellation is not null) { cancellation.Cancel(); return; }
        if (sending is not null) { StatusText.Text = "Finish or cancel sending before receiving."; return; }
        receiveFolder ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "OpenShare");
        var folder = receiveFolder;
        cancellation = new CancellationTokenSource();
        try
        {
            using var session = new ReceiveSession();
            listener = new TcpListener(IPAddress.Any, 0);
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var addresses = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
                .Select(a => session.Code(a.Address.ToString(), port).ToString()).Distinct().ToArray();
            PairCode.Text = addresses.Length == 0 ? session.Code("127.0.0.1", port).ToString() : string.Join("\n", addresses);
            ListenButton.Content = "Stop receiving";
            Progress.Value = 0;
            StatusText.Text = $"Waiting for one file in {folder}";
            var progress = TransferProgress(true);
            var receipt = await TransferEngine.ReceiveAsync(listener, folder, session, ApproveIncoming, progress, cancellation.Token);
            TransferHistory.Append(receipt);
            StatusText.Text = $"Received and verified {receipt.Name}. Receipt saved.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { StatusText.Text = "Receiving stopped. Incomplete data was removed."; }
        catch (OperationCanceledException) { StatusText.Text = "The connection or approval timed out. Start receiving to try again."; }
        catch (Exception ex) { Diagnostics.Record("receive-failed", ex); StatusText.Text = $"Receive failed: {ex.Message}"; }
        finally
        {
            listener?.Stop(); listener = null;
            cancellation.Dispose(); cancellation = null;
            PairCode.Text = "Not listening";
            ListenButton.Content = "Start receiving";
        }
    }
    private void Folder_Click(object sender, RoutedEventArgs e) { using var dialog = new Forms.FolderBrowserDialog { Description = "Choose where received files are saved" }; if (dialog.ShowDialog() == Forms.DialogResult.OK) { receiveFolder = dialog.SelectedPath; StatusText.Text = $"Receive folder: {receiveFolder}"; } }
    private void File_Click(object sender, RoutedEventArgs e) { using var dialog = new Forms.OpenFileDialog { Title = "Choose a file to send" }; if (dialog.ShowDialog() == Forms.DialogResult.OK) { selectedFile = dialog.FileName; FileText.Text = selectedFile; } }
    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (sending is not null) { sending.Cancel(); return; }
        if (cancellation is not null) { StatusText.Text = "Stop receiving before sending."; return; }
        var path = selectedFile;
        if (path is null || !File.Exists(path)) { StatusText.Text = "Choose a file first."; return; }
        PairingCode pairing;
        try { pairing = PairingCode.Parse(CodeBox.Text); }
        catch (FormatException ex) { StatusText.Text = ex.Message; return; }
        var button = (System.Windows.Controls.Button)sender;
        sending = new CancellationTokenSource();
        button.Content = "Cancel sending";
        Progress.Value = 0;
        StatusText.Text = "Preparing file. The receiver must approve it before saving.";
        try { transferSize = new FileInfo(path).Length; var progress = TransferProgress(false); var receipt = await TransferEngine.SendAsync(path, pairing, progress, sending.Token); TransferHistory.Append(receipt); StatusText.Text = "Receiver confirmed: file saved and verified. Receipt saved."; }
        catch (OperationCanceledException) when (sending.IsCancellationRequested) { StatusText.Text = "Sending cancelled. Delivery was not confirmed."; }
        catch (Exception ex) { Diagnostics.Record("send-failed", ex); StatusText.Text = $"Transfer not confirmed: {ex.Message}"; }
        finally { sending.Dispose(); sending = null; button.Content = "Send file"; }
    }
    private Task<bool> ApproveIncoming(TransferHeader header, CancellationToken token)
    {
        transferSize = header.Length;
        token.ThrowIfCancellationRequested();
        var owner = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var uiThread = ApprovalNative.GetCurrentThreadId();
        using var dismiss = token.Register(() => ApprovalNative.EnumThreadWindows(uiThread, (popup, _) =>
        {
            if (ApprovalNative.GetWindow(popup, 4) == owner) ApprovalNative.PostMessage(popup, 0x0111, new IntPtr(7), IntPtr.Zero);
            return true;
        }, IntPtr.Zero));
        var answer = System.Windows.MessageBox.Show(this,
            $"Save this incoming file?\n\n{header.Name}\n{header.Length:N0} bytes\n\nDestination: {receiveFolder}\n\nOnly accept files you expect. Respond within 20 seconds.",
            "Incoming file", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        token.ThrowIfCancellationRequested();
        return Task.FromResult(answer == MessageBoxResult.Yes);
    }
    private IProgress<double> TransferProgress(bool receiving)
    {
        var clock = new System.Diagnostics.Stopwatch();
        long lastUpdate = -100;
        return new Progress<double>(value =>
        {
            if (receiving ? cancellation is null : sending is null) return;
            if (!clock.IsRunning) clock.Start();
            if (value < 1 && clock.ElapsedMilliseconds - lastUpdate < 100) return;
            lastUpdate = clock.ElapsedMilliseconds;
            Progress.Value = value;
            if (value >= 1) { StatusText.Text = "Verifying saved file and confirming delivery..."; return; }
            var transferred = transferSize * value;
            var speed = transferred / Math.Max(0.1, clock.Elapsed.TotalSeconds);
            var remaining = speed > 0 ? Math.Ceiling((transferSize - transferred) / speed) : 0;
            StatusText.Text = $"{(receiving ? "Receiving" : "Sending")} {value:P0}\n{SizeText(transferred)} of {SizeText(transferSize)}\n{SizeText(speed)}/s - about {remaining:N0}s remaining";
        });
    }
    private static string SizeText(double bytes) => bytes >= 1024 * 1024 * 1024 ? $"{bytes / (1024 * 1024 * 1024):F1} GiB" : bytes >= 1024 * 1024 ? $"{bytes / (1024 * 1024):F1} MiB" : $"{bytes / 1024:F1} KiB";
    private void History_Click(object sender, RoutedEventArgs e)
    {
        var path = TransferHistory.DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) File.WriteAllText(path, "[]");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
    }
}
internal static class ApprovalNative
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern IntPtr GetWindow(IntPtr window, uint command);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();
    internal delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool EnumThreadWindows(uint thread, EnumWindowCallback callback, IntPtr parameter);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
