using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace NetMonitor;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };

    public MainWindow()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    private void Refresh()
    {
        var connections = ConnectionReader.ReadTcp().AsEnumerable();

        if (OnlyEstablished.IsChecked == true)
            connections = connections.Where(c => c.State == "ESTABLISHED");

        string filter = FilterBox.Text.Trim();
        if (filter.Length > 0)
            connections = connections.Where(c =>
                c.Process.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                c.Remote.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                c.Local.Contains(filter, StringComparison.OrdinalIgnoreCase));

        var list = connections.OrderBy(c => c.Process).ToList();
        Grid.ItemsSource = list;
        StatusText.Text = $"{list.Count} bağlantı  |  {DateTime.Now:HH:mm:ss}";
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => Refresh();
    private void Option_Changed(object sender, RoutedEventArgs e) => Refresh();
}
