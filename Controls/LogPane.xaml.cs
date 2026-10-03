using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TyranoToolKit.Controls;

public partial class LogPane : UserControl
{
    private int _lineCount;
    private const int MaxLines = 4000;

    public LogPane()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 追加一行日志。stream 决定颜色：stdout/done=前景灰，stderr/error=红，stage 高亮为青色。
    /// </summary>
    public void Append(string stream, string line)
    {
        if (string.IsNullOrEmpty(line)) return;

        Dispatcher.Invoke(() =>
        {
            var color = stream switch
            {
                "stderr" or "error" => (Brush)Application.Current.FindResource("ErrorBrush"),
                "done" => (Brush)Application.Current.FindResource("SuccessBrush"),
                "stage" => (Brush)Application.Current.FindResource("WarnBrush"),
                _ => (Brush)Application.Current.FindResource("FgBrush"),
            };

            if (_lineCount > 0)
                LogText.Inlines.Add(new System.Windows.Documents.LineBreak());

            LogText.Inlines.Add(new System.Windows.Documents.Run
            {
                Text = line,
                Foreground = color,
            });
            _lineCount++;

            if (_lineCount > MaxLines)
            {
                LogText.Text = "";
                _lineCount = 0;
            }

            Scroller.ScrollToBottom();
        });
    }

    public void Clear()
    {
        Dispatcher.Invoke(() =>
        {
            LogText.Inlines.Clear();
            LogText.Text = "";
            _lineCount = 0;
        });
    }

    private void ClearBtn_Click(object sender, RoutedEventArgs e) => Clear();
}
