using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace TyranoToolKit.Controls;

public partial class StepBar : UserControl
{
    public class StepInfo
    {
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
    }

    private List<StepInfo> _steps = new();
    private int _currentStep = -1;

    public IReadOnlyList<StepInfo> Steps
    {
        get => _steps;
        set
        {
            _steps = value?.ToList() ?? new List<StepInfo>();
            _currentStep = -1;
            Rebuild();
        }
    }

    public int CurrentStep
    {
        get => _currentStep;
        set
        {
            _currentStep = value;
            ApplyState();
        }
    }

    public StepBar()
    {
        InitializeComponent();
    }

    private void Rebuild()
    {
        Root.Children.Clear();
        for (int i = 0; i < _steps.Count; i++)
        {
            if (i > 0)
            {
                var line = new Border
                {
                    Width = 24,
                    Height = 1,
                    Background = (Brush)Application.Current.FindResource("BorderBrush"),
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 14, 0, 0),
                };
                Root.Children.Add(line);
            }

            var step = _steps[i];
            var panel = new StackPanel { Orientation = Orientation.Horizontal };

            var circleGrid = new Grid { Width = 28, Height = 28 };
            var circle = new Ellipse
            {
                Width = 28,
                Height = 28,
                Fill = (Brush)Application.Current.FindResource("BgHoverBrush"),
                Stroke = (Brush)Application.Current.FindResource("BorderBrush"),
                StrokeThickness = 1,
            };
            circleGrid.Children.Add(circle);

            var indexText = new TextBlock
            {
                Text = (i + 1).ToString(),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.FindResource("FgMutedBrush"),
            };
            circleGrid.Children.Add(indexText);

            var checkText = new TextBlock
            {
                Text = "\u2713",
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.White,
                Visibility = Visibility.Collapsed,
            };
            circleGrid.Children.Add(checkText);

            var textPanel = new StackPanel
            {
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var titleText = new TextBlock
            {
                Text = step.Title,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.FindResource("FgMutedBrush"),
            };
            var descText = new TextBlock
            {
                Text = step.Description,
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 0),
                Foreground = (Brush)Application.Current.FindResource("FgMutedBrush"),
            };
            textPanel.Children.Add(titleText);
            textPanel.Children.Add(descText);

            panel.Children.Add(circleGrid);
            panel.Children.Add(textPanel);
            Root.Children.Add(panel);
        }
        ApplyState();
    }

    private void ApplyState()
    {
        int idx = 0;
        foreach (FrameworkElement child in Root.Children.OfType<FrameworkElement>())
        {
            if (child is not StackPanel panel) continue;
            if (idx >= _steps.Count) break;

            Grid? circleGrid = null;
            foreach (var c in panel.Children.OfType<Grid>())
            {
                circleGrid = c;
                break;
            }
            if (circleGrid == null) { idx++; continue; }

            Ellipse? circle = null;
            TextBlock? indexText = null;
            TextBlock? checkText = null;
            foreach (var c in circleGrid.Children)
            {
                if (c is Ellipse el) circle = el;
                else if (c is TextBlock tb)
                {
                    if (tb.Text == "\u2713") checkText = tb;
                    else indexText = tb;
                }
            }

            StackPanel? textPanel = null;
            foreach (var c in panel.Children)
            {
                if (c is StackPanel sp && sp != panel) { textPanel = sp; break; }
            }
            TextBlock? titleText = textPanel?.Children.OfType<TextBlock>().FirstOrDefault();

            bool isDone = idx < _currentStep;
            bool isCurrent = idx == _currentStep;

            if (circle != null)
                circle.Fill = (isDone || isCurrent)
                    ? (Brush)Application.Current.FindResource("AccentBrush")
                    : (Brush)Application.Current.FindResource("BgHoverBrush");
            if (indexText != null)
                indexText.Visibility = isDone ? Visibility.Collapsed : Visibility.Visible;
            if (checkText != null)
                checkText.Visibility = isDone ? Visibility.Visible : Visibility.Collapsed;
            if (titleText != null)
                titleText.Foreground = (isDone || isCurrent)
                    ? (Brush)Application.Current.FindResource("FgBrush")
                    : (Brush)Application.Current.FindResource("FgMutedBrush");
            idx++;
        }
    }
}
