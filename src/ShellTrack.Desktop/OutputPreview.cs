using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ShellTrack.Desktop;

/// <summary>Renders log lines at the available width and clips the final visual rows.</summary>
public sealed class OutputPreview : UserControl
{
    private readonly ItemsControl lines = new() { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Top };
    private readonly ScrollViewer viewport;
    private bool loaded, bottomQueued;
    public static readonly DependencyProperty LinesProperty = DependencyProperty.Register(nameof(Lines), typeof(object), typeof(OutputPreview), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty LineTemplateProperty = DependencyProperty.Register(nameof(LineTemplate), typeof(DataTemplate), typeof(OutputPreview), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty RowsProperty = DependencyProperty.Register(nameof(Rows), typeof(int), typeof(OutputPreview), new PropertyMetadata(5, Changed));
    public static readonly DependencyProperty LineHeightProperty = DependencyProperty.Register(nameof(LineHeight), typeof(double), typeof(OutputPreview), new PropertyMetadata(20d, Changed));
    public object? Lines { get => GetValue(LinesProperty); set => SetValue(LinesProperty, value); }
    public DataTemplate? LineTemplate { get => (DataTemplate?)GetValue(LineTemplateProperty); set => SetValue(LineTemplateProperty, value); }
    public int Rows { get => (int)GetValue(RowsProperty); set => SetValue(RowsProperty, value); }
    public double LineHeight { get => (double)GetValue(LineHeightProperty); set => SetValue(LineHeightProperty, value); }

    public OutputPreview()
    {
        viewport = new ScrollViewer
        {
            Content = lines, HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollMode = ScrollMode.Enabled,
            ZoomMode = ZoomMode.Disabled, IsHitTestVisible = false, IsTabStop = false, Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Top
        };
        Content = viewport; IsTabStop = false;
        Loaded += (_, _) => { loaded = true; QueueBottom(); };
        Unloaded += (_, _) => loaded = false;
        LayoutUpdated += (_, _) =>
        {
            if (viewport.ExtentHeight > 0)
            {
                double height = Math.Min(Math.Clamp(Rows, 1, 30) * LineHeight, Math.Max(LineHeight, viewport.ExtentHeight));
                if (Math.Abs(Height - height) > 0.1) Height = height;
            }
            if (Math.Abs(viewport.VerticalOffset - viewport.ScrollableHeight) > 0.1) QueueBottom();
        };
        SizeChanged += (_, _) => QueueBottom();
    }
    private static void Changed(DependencyObject source, DependencyPropertyChangedEventArgs args) => ((OutputPreview)source).Update();
    private void Update()
    {
        lines.ItemTemplate = LineTemplate; lines.ItemsSource = Lines;
        Height = Math.Clamp(Rows, 1, 30) * LineHeight;
        QueueBottom();
    }
    private void QueueBottom()
    {
        if (!loaded || bottomQueued) return;
        bottomQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            bottomQueued = false;
            if (!loaded) return;
            viewport.UpdateLayout();
            viewport.ChangeView(null, viewport.ScrollableHeight, null, true);
        });
    }
    internal object Diagnostics() => new { rows = Rows, width = ActualWidth, height = ActualHeight, contentHeight = viewport.ExtentHeight,
        offset = viewport.VerticalOffset, bottom = viewport.ScrollableHeight, lineHeight = LineHeight };
}
