using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using Forms = System.Windows.Forms;
namespace OpenShare;
public partial class MainWindow : Window
{
    private TcpListener? listener; private CancellationTokenSource? cancellation; private string? receiveFolder; private string? selectedFile; private int port;
    public MainWindow() { InitializeComponent(); Opacity = 0; Loaded += (_, _) => BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240))); }
    private async void Listen_Click(object sender, RoutedEventArgs e) { if (listener is not null) return; receiveFolder ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "OpenShare"); port = Random.Shared.Next(39000, 49000); listener = new TcpListener(IPAddress.Any, port); listener.Start(); cancellation = new CancellationTokenSource(); PairCode.Text = port.ToString(); ListenButton.Content = "Listening"; StatusText.Text = $"Waiting for a transfer in {receiveFolder}"; try { var progress = new Progress<double>(value => Progress.Value = value); var header = await TransferEngine.ReceiveAsync(listener, receiveFolder, progress, cancellation.Token); StatusText.Text = $"Received and verified {header.Name}"; } catch (OperationCanceledException) { StatusText.Text = "Receiving stopped."; } catch (Exception ex) { StatusText.Text = ex.Message; } finally { listener.Stop(); listener = null; cancellation.Dispose(); cancellation = null; ListenButton.Content = "Start receiving"; } }
    private void Folder_Click(object sender, RoutedEventArgs e) { using var dialog = new Forms.FolderBrowserDialog { Description = "Choose where received files are saved" }; if (dialog.ShowDialog() == Forms.DialogResult.OK) { receiveFolder = dialog.SelectedPath; StatusText.Text = $"Receive folder: {receiveFolder}"; } }
    private void File_Click(object sender, RoutedEventArgs e) { using var dialog = new Forms.OpenFileDialog { Title = "Choose a file to send" }; if (dialog.ShowDialog() == Forms.DialogResult.OK) { selectedFile = dialog.FileName; FileText.Text = selectedFile; } }
    private async void Send_Click(object sender, RoutedEventArgs e) { if (selectedFile is null || !File.Exists(selectedFile)) { StatusText.Text = "Choose a file first."; return; } var parts = CodeBox.Text.Trim().Split(':', 2); if (parts.Length != 2 || !int.TryParse(parts[1], out var receiverPort) || receiverPort is < 1 or > 65535) { StatusText.Text = "Enter the receiver address as IP:code, for example 192.168.1.24:40123."; return; } StatusText.Text = "Sending securely…"; try { var progress = new Progress<double>(value => Progress.Value = value); await TransferEngine.SendAsync(selectedFile, parts[0], receiverPort, progress); StatusText.Text = "Transfer complete and verified."; } catch (Exception ex) { StatusText.Text = $"Transfer failed: {ex.Message}"; } }
}
