using System.Windows;
using System.Windows.Documents;

namespace TyranoToolKit;

public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();
    }

    private void Link_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Hyperlink link && link.NavigateUri != null)
        {
            try { System.Diagnostics.Process.Start("explorer.exe", link.NavigateUri.AbsoluteUri); } catch { }
        }
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
