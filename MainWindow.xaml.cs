using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using Forms = System.Windows.Forms;
namespace OpenShare;
public partial class MainWindow : Window
{
    private TcpListener? listener; private CancellationTokenSource? cancellation; private CancellationTokenSource? sending; private string? receiveFolder; private string? selectedFile; private int port;
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
            var progress = new Progress<double>(value => Progress.Value = value);
            var header = await TransferEngine.ReceiveAsync(listener, folder, session, ApproveIncoming, progress, cancellation.Token);
            StatusText.Text = $"Received and verified {header.Name}";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { StatusText.Text = "Receiving stopped. Incomplete data was removed."; }
        catch (OperationCanceledException) { StatusText.Text = "The connection or approval timed out. Start receiving to try again."; }
        catch (Exception ex) { StatusText.Text = $"Receive failed: {ex.Message}"; }
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
        try { var progress = new Progress<double>(value => Progress.Value = value); await TransferEngine.SendAsync(path, pairing, progress, sending.Token); StatusText.Text = "Receiver confirmed: file saved and verified."; }
        catch (OperationCanceledException) when (sending.IsCancellationRequested) { StatusText.Text = "Sending cancelled. Delivery was not confirmed."; }
        catch (Exception ex) { StatusText.Text = $"Transfer not confirmed: {ex.Message}"; }
        finally { sending.Dispose(); sending = null; button.Content = "Send file"; }
    }
    private Task<bool> ApproveIncoming(TransferHeader header, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var answer = System.Windows.MessageBox.Show(this,
            $"Save this incoming file?\n\n{header.Name}\n{header.Length:N0} bytes\n\nDestination: {receiveFolder}\n\nOnly accept files you expect. Respond within 20 seconds.",
            "Incoming file", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        token.ThrowIfCancellationRequested();
        return Task.FromResult(answer == MessageBoxResult.Yes);
    }
}
