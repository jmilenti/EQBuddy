using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace EQBuddy.Lite;

/// <summary>One coloured stretch of a feed row. Bold is the XP rows' headline weight.</summary>
public sealed record FeedSpan(string Text, Brush Color, bool Bold = false);

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

    /// <summary>Draw a thin rounded frame around this row (the kill summaries) so it
    /// stands apart from the stream it summarises. Null = no frame, no padding — the
    /// ordinary rows keep their exact geometry.</summary>
    public Brush? Frame { get; init; }

    // Bound by the row template — alignment, margins, and frame as WPF wants them.
    // The row aligns as a BLOCK (shrink-wrapped Border), not by TextAlignment: a
    // block can wear a frame, and a right-aligned block puts the row's END flush
    // against the edge, which is what lets a suffix timestamp line up.
    public HorizontalAlignment HAlign => Right ? HorizontalAlignment.Right : HorizontalAlignment.Left;

    public Thickness Pad => Gap ? new Thickness(0, 4, 0, 0) : default;

    public Thickness FrameThickness => Frame is null ? default : new Thickness(1);

    public Thickness FramePad => Frame is null ? default : new Thickness(5, 1, 5, 1);

    /// <summary>One flat chat-safe line to put on the clipboard when the row is
    /// clicked — only the kill summaries set it. Null = the row is not clickable.</summary>
    public string? Copy { get; init; }

    /// <summary>Hover hint, only on clickable rows (a null ToolTip simply never shows).</summary>
    public string? Tip => Copy is null ? null : "Click to copy for game chat";

    /// <summary>Hand over clickable rows; null lets the list's cursor through.</summary>
    public Cursor? Pointer => Copy is null ? null : Cursors.Hand;

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
            block.Inlines.Add(new Run(span.Text)
            {
                Foreground = span.Color,
                FontWeight = span.Bold ? FontWeights.Bold : FontWeights.Normal,
            });
    }
}
