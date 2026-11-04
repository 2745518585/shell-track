using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Text;
using ShellTrack.Core;
using Windows.Foundation;
using Windows.ApplicationModel.DataTransfer;

namespace ShellTrack.Desktop;

public sealed partial class SelectableLogView : UserControl
{
    private readonly LogDocument snapshot = new();
    private bool updating, pointerDown, gutterDirty = true, gutterQueued;
    private readonly List<TextBlock> numbers = [];
    private ScrollViewer? scroller;
    public event EventHandler? TextSelected;
    // Freeze only a nonempty text selection in the displayed snapshot.
    public bool HasSelection => snapshot.Text.Length > 0 && Editor.TextDocument.Selection.Length > 0 && SelectedText.Length > 0;
    public bool IsSelecting => pointerDown || HasSelection;
    public int LineCount => snapshot.Lines.Count;
    public object Diagnostics() => new { lines = LineCount, characters = snapshot.Text.Length, pointerDown, hasSelection = HasSelection,
        selectionStart = Editor.TextDocument.Selection.StartPosition, selectionEnd = Editor.TextDocument.Selection.EndPosition,
        viewportOffset = scroller?.VerticalOffset, visibleLineNumbers = numbers.Where(n => n.Visibility == Visibility.Visible).Select(n => n.Text).ToArray() };
    public string Text => snapshot.Text.Replace("\r", "\n");
    public string SelectedText
    {
        get { return snapshot.SelectedText(Editor.TextDocument.Selection.StartPosition, Editor.TextDocument.Selection.EndPosition); }
    }
    public SelectableLogView()
    {
        InitializeComponent();
        var format = Editor.TextDocument.GetDefaultParagraphFormat();
        format.SetLineSpacing(LineSpacingRule.Exactly, 15); // points: 20 DIPs, matching card previews
        format.SpaceBefore = format.SpaceAfter = 0;
        Editor.TextDocument.SetDefaultParagraphFormat(format);
        Editor.CopyingToClipboard += (_, e) =>
        {
            e.Handled = true;
            if (!HasSelection) return;
            var data = new DataPackage(); data.SetText(SelectedText); Clipboard.SetContent(data);
        };
        Editor.AddHandler(PointerPressedEvent, new PointerEventHandler((_, e) =>
        { if (e.GetCurrentPoint(Editor).Properties.IsLeftButtonPressed) pointerDown = true; }), true);
        Editor.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => pointerDown = false), true);
        Editor.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => pointerDown = false), true);
        SizeChanged += (_, _) => { Gutter.Clip = new RectangleGeometry { Rect = new Rect(0, 0, Gutter.ActualWidth, ActualHeight) }; gutterDirty = true; };
        Loaded += (_, _) =>
        {
            Editor.ApplyTemplate();
            AttachScroller();
            gutterDirty = true;
        };
        LayoutUpdated += (_, _) =>
        {
            // The output pane starts collapsed; its editor template may be
            // created only after a task is selected, later than Loaded.
            if (IsLoaded && scroller is null) AttachScroller();
            if (gutterDirty && IsLoaded) { gutterDirty = false; DrawGutter(); }
        };
    }
    public void SetLines(IReadOnlyList<NumberedLogLine> lines, bool follow)
    {
        if (IsSelecting) return;
        updating = true;
        try
        {
            string previous = snapshot.Text;
            int prefix = snapshot.Update(lines);
            if (snapshot.Text != previous)
            {
                int caret = Editor.TextDocument.Selection.StartPosition;
                WriteDocument(() => Editor.TextDocument.GetRange(prefix, previous.Length).SetText(TextSetOptions.None, snapshot.Text[prefix..]));
                Editor.TextDocument.Selection.SetRange(Math.Min(caret, snapshot.Text.Length), Math.Min(caret, snapshot.Text.Length));
            }
            gutterDirty = true;
            if (follow) ScrollToEnd();
        }
        finally { updating = false; }
    }
    public void Clear()
    {
        updating = true;
        try
        {
            pointerDown = false;
            WriteDocument(() => Editor.TextDocument.SetText(TextSetOptions.None, "")); snapshot.Update([]);
            Gutter.Children.Clear(); numbers.Clear(); gutterDirty = true;
        }
        finally { updating = false; }
    }
    public void ClearSelection() => Editor.TextDocument.Selection.SetRange(snapshot.Text.Length, snapshot.Text.Length);
    private void WriteDocument(Action write)
    {
        // WinUI also rejects programmatic SetText while IsReadOnly is true.
        // This synchronous UI-thread scope never yields to user input; restore
        // read-only even if a native write fails.
        Editor.IsReadOnly = false;
        try { write(); }
        finally { Editor.IsReadOnly = true; }
    }
    public void ScrollToEnd()
    {
        Editor.TextDocument.GetRange(snapshot.Text.Length, snapshot.Text.Length).ScrollIntoView(PointOptions.None);
        gutterDirty = true;
    }
    private void Editor_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (!updating && HasSelection) TextSelected?.Invoke(this, EventArgs.Empty);
    }
    private void QueueGutter()
    {
        if (gutterQueued || !IsLoaded) return;
        gutterQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            gutterQueued = false;
            if (!IsLoaded) return;
            gutterDirty = false; DrawGutter();
        });
    }
    private void AttachScroller()
    {
        if (scroller is not null) return;
        scroller = FindScroller(Editor);
        if (scroller is not null)
        {
            scroller.ViewChanged += (_, _) => { gutterDirty = true; QueueGutter(); };
            gutterDirty = true;
        }
    }
    private void DrawGutter()
    {
        if (Editor.ActualHeight <= 0 || snapshot.Lines.Count == 0) return;
        // TOM coordinates belong to the editor's scrolling content, not the
        // surrounding XAML viewport. Translate using the ScrollViewer offset.
        double offset = scroller?.VerticalOffset ?? 0;
        int position = Editor.TextDocument.GetRangeFromPoint(new Point(0, offset), PointOptions.ClientCoordinates).StartPosition;
        int first = Math.Max(0, snapshot.LineAt(position) - 1), used = 0;
        // Only create labels for the visible viewport, including a partially
        // visible first logical line. Wrapped continuation rows have no label.
        for (int i = Math.Max(0, first); i < snapshot.Lines.Count; i++)
        {
            Editor.TextDocument.GetRange(snapshot.Starts[i], snapshot.Starts[i]).GetPoint(
                HorizontalCharacterAlignment.Left, VerticalCharacterAlignment.Baseline,
                PointOptions.ClientCoordinates | PointOptions.AllowOffClient, out Point point);
            double baseline = point.Y - offset;
            if (baseline > Editor.ActualHeight + 20) break;
            if (baseline < 0) continue;
            if (used == numbers.Count)
            {
                var number = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 13, LineHeight = 20,
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight, TextAlignment = TextAlignment.Right, Width = 54,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"] };
                numbers.Add(number); Gutter.Children.Add(number);
            }
            var label = numbers[used++]; label.Text = snapshot.Lines[i].Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            label.Visibility = Visibility.Visible; label.Measure(new Size(54, 20));
            Canvas.SetLeft(label, 6); Canvas.SetTop(label, baseline - label.BaselineOffset);
        }
        for (int i = used; i < numbers.Count; i++) numbers[i].Visibility = Visibility.Collapsed;
    }
    private static ScrollViewer? FindScroller(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer scroll) return scroll;
            if (FindScroller(child) is { } nested) return nested;
        }
        return null;
    }
}
