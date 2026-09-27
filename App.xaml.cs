using System.Windows;
namespace OpenShare;
public partial class App : System.Windows.Application { public App() => Startup += (_, _) => new MainWindow().Show(); }
