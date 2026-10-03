using System.Windows;
using System.Windows.Controls;
using TyranoToolKit.Views;

namespace TyranoToolKit;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => NavigateToPage(NavElectron);
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0) return;
        if (e.AddedItems[0] is ListBoxItem item)
            NavigateToPage(item);
    }

    private void NavigateToPage(ListBoxItem navItem)
    {
        if (!IsLoaded || ContentFrame == null) return;

        Page page = navItem.Name switch
        {
            "NavElectron" => new ElectronPage(),
            "NavApk" => new ApkBuilderPage(),
            _ => new ElectronPage()
        };
        ContentFrame.Navigate(page);
    }

    private void AboutBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new AboutDialog
        {
            Owner = this,
        };
        dlg.ShowDialog();
    }
}
