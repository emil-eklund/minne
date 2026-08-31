using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace MailSearch.App;

/// <summary>TextBlock that renders a SnippetRun list with matched terms in bold.</summary>
public sealed class HighlightTextBlock : TextBlock
{
    public static readonly StyledProperty<IReadOnlyList<SnippetRun>?> RunsProperty =
        AvaloniaProperty.Register<HighlightTextBlock, IReadOnlyList<SnippetRun>?>(nameof(Runs));

    static HighlightTextBlock()
    {
        RunsProperty.Changed.AddClassHandler<HighlightTextBlock>((tb, _) => tb.Rebuild());
    }

    public IReadOnlyList<SnippetRun>? Runs
    {
        get => GetValue(RunsProperty);
        set => SetValue(RunsProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(TextBlock);

    private void Rebuild()
    {
        var inlines = new InlineCollection();
        if (Runs is { } runs)
            foreach (var r in runs)
                inlines.Add(new Run(r.Text) { FontWeight = r.Highlight ? FontWeight.Bold : FontWeight.Normal });
        Inlines = inlines;
    }
}
