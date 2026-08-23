using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace EQBuddy.Lite;

/// <summary>One coloured stretch of a feed row.</summary>
public sealed record FeedSpan(string Text, Brush Color);

/// <summary>One row of a FEED list: the line broken into coloured stretches — a dim
/// timestamp, the line itself in whatever colour its kind earns, and the ability or spell
/// picked out inside it. Bound by the FeedRowTemplate resource in MainWindow.xaml through
/// <see cref="FeedText"/>, because a TextBlock's Inlines are not a bindable property.</summary>
public sealed record FeedRow(IReadOnlyList<FeedSpan> Spans)
{
    public FeedRow(string text, Brush color) : this([new FeedSpan(text, color)]) { }

    /// <summary>Hug the right edge — an INCOMING row in the chat layout, where what is
    /// done to you and what you do read as two sides of a conversation.</summary>
    public bool Right { get; init; }

    /// <summary>Leave air above this row: the side changed here. Without it the two
    /// columns run together and the layout stops reading as a back-and-forth.</summary>
    public bool Gap { get; init; }

    /// <summary>Bound by the row template — Alignment and Margin as WPF wants them.</summary>
    public TextAlignment Align => Right ? TextAlignment.Right : TextAlignment.Left;

    public Thickness Pad => Gap ? new Thickness(0, 4, 0, 0) : default;

    /// <summary>The whole line as plain text — what a UIA client reads off the row, and
    /// what makes the list testable from outside.</summary>
    public string Text { get; } = string.Concat(Spans.Select(s => s.Text));
}

/// <summary>Attaches a <see cref="FeedRow"/>'s spans to a TextBlock as Runs.</summary>
public static class FeedText
{
    public static readonly DependencyProperty SpansProperty = DependencyProperty.RegisterAttached(
        "Spans", typeof(IReadOnlyList<FeedSpan>), typeof(FeedText),
        new PropertyMetadata(null, OnSpansChanged));

    public static void SetSpans(DependencyObject d, IReadOnlyList<FeedSpan>? value) =>
        d.SetValue(SpansProperty, value);

    public static IReadOnlyList<FeedSpan>? GetSpans(DependencyObject d) =>
        (IReadOnlyList<FeedSpan>?)d.GetValue(SpansProperty);

    private static void OnSpansChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block) return;
        block.Inlines.Clear();
        if (e.NewValue is not IReadOnlyList<FeedSpan> spans) return;
        foreach (var span in spans)
            block.Inlines.Add(new Run(span.Text) { Foreground = span.Color });
    }
}
