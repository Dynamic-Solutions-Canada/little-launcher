using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LittleLauncher.Controls;

/// <summary>
/// Fits an icon tile's name into its two lines without breaking a word: Latin names wrap between
/// words, a single word too long for the tile is drawn a little smaller instead of being split
/// ("Configurações", not "Configuraçõe / s"), and Chinese or Japanese names without spaces are split
/// into two even lines instead of leaving one character alone ("文件资源 / 管理器", not
/// "文件资源管理 / 器").
/// </summary>
/// <remarks>
/// <para>Attach <c>IsEnabled</c> to the label in XAML, or call <see cref="Attach"/> on one built in
/// code. The label's <c>MaxWidth</c> and <c>FontSize</c> when attached are its budget and its normal
/// size; the fit only ever narrows the width and shrinks the font from there, so the tile's geometry
/// never changes. It refits whenever the text changes, which covers renames and recycled
/// containers, and once the label is live, since text measures zero before that.</para>
/// <para>Measuring a string, not laid-out content: the widths come from a detached TextBlock with
/// the label's font, so nothing here feeds back into the layout that produced it.</para>
/// </remarks>
public sealed class IconLabelFit : DependencyObject
{
    /// <summary>The smallest size a long word shrinks to; below it the word trims with an ellipsis.</summary>
    private const double MinFontSize = 9;

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(IconLabelFit), new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly DependencyProperty BudgetProperty = DependencyProperty.RegisterAttached(
        "Budget", typeof(object), typeof(IconLabelFit), new PropertyMetadata(null));

    /// <summary>Characters that may not start a line in Chinese or Japanese (closing punctuation, small kana, the long vowel mark).</summary>
    private const string NoLineStart = "、。，．：；？！）」』】〕〉》ーぁぃぅぇぉっゃゅょァィゥェォッャュョ・";

    /// <summary>Gets whether the label fits its name to the tile.</summary>
    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    /// <summary>Fits the label's name to the tile; set on the icon-mode name label.</summary>
    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    /// <summary>Fits a label built in code, as the XAML property does for a template's.</summary>
    public static void Attach(TextBlock label) => SetIsEnabled(label, true);

    private static void OnIsEnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not TextBlock label || args.NewValue is not true || label.GetValue(BudgetProperty) is not null)
        {
            return;
        }

        label.SetValue(BudgetProperty, new Budget(label.MaxWidth, label.FontSize));
        label.TextWrapping = TextWrapping.WrapWholeWords;
        label.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => Fit(label));
        label.Loaded += (_, _) => Fit(label);
        Fit(label);
    }

    /// <summary>
    /// Sets the label's font size and width for its current text. Does nothing until the label can
    /// measure text, and leaves an empty label at its normal size.
    /// </summary>
    private static void Fit(TextBlock label)
    {
        if (label.GetValue(BudgetProperty) is not Budget budget || double.IsInfinity(budget.Width))
        {
            return;
        }

        // Start from the normal look every time: a renamed or recycled label must not keep the last
        // name's smaller font or narrower width.
        label.FontSize = budget.FontSize;
        label.MaxWidth = budget.Width;

        string text = label.Text ?? "";
        double full = Measure(label, text, budget.FontSize);
        if (full <= 0 || full <= budget.Width)
        {
            return;
        }

        if (text.Contains(' '))
        {
            // Wraps between words; only a word wider than the tile needs help.
            double longestWord = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Max(word => Measure(label, word, budget.FontSize));
            ShrinkToFit(label, budget, longestWord);
            return;
        }

        if (HasCjk(text))
        {
            label.MaxWidth = BalancedWidth(label, text, budget);
            return;
        }

        // One word longer than the tile: smaller rather than split.
        ShrinkToFit(label, budget, full);
    }

    /// <summary>Shrinks the font just enough for a word <paramref name="wordWidth"/> wide, down to <see cref="MinFontSize"/>.</summary>
    private static void ShrinkToFit(TextBlock label, Budget budget, double wordWidth)
    {
        if (wordWidth <= budget.Width)
        {
            return;
        }

        double size = Math.Floor(budget.FontSize * budget.Width / wordWidth * 10) / 10;
        label.FontSize = Math.Max(MinFontSize, size);
    }

    /// <summary>
    /// The width that splits an unspaced Chinese or Japanese name into two even lines: the split
    /// point is chosen so the longer line is as short as it can be, never before a character that may
    /// not start a line, and the label is made just wide enough for that line. A name that needs more
    /// than two lines keeps the full width, and trims as before.
    /// </summary>
    private static double BalancedWidth(TextBlock label, string text, Budget budget)
    {
        double best = budget.Width;
        for (int split = 1; split < text.Length; split++)
        {
            if (NoLineStart.Contains(text[split]) || char.IsLowSurrogate(text[split]))
            {
                continue;
            }

            double longer = Math.Max(Measure(label, text[..split], budget.FontSize), Measure(label, text[split..], budget.FontSize));
            best = Math.Min(best, longer);
        }

        // Half a character of slack, so rounding never pushes the line's last character down.
        return Math.Min(budget.Width, Math.Ceiling(best + (budget.FontSize / 2)));
    }

    /// <summary>True when the text has Chinese, Japanese or Korean characters, which wrap between any two.</summary>
    private static bool HasCjk(string text) => text.Any(c =>
        c is >= '぀' and <= 'ヿ'      // Hiragana, Katakana
        or >= '㐀' and <= '鿿'        // CJK ideographs
        or >= '가' and <= '힯'        // Hangul
        or >= '豈' and <= '﫿');      // CJK compatibility ideographs

    /// <summary>How wide <paramref name="text"/> draws on one line in the label's font at <paramref name="fontSize"/>; 0 before the label is live.</summary>
    private static double Measure(TextBlock label, string text, double fontSize)
    {
        var probe = new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            FontFamily = label.FontFamily,
            FontWeight = label.FontWeight,
            TextWrapping = TextWrapping.NoWrap,
        };
        probe.Measure(new global::Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        return probe.DesiredSize.Width;
    }

    /// <summary>A label's width budget and normal font size, as they were when the fit was attached.</summary>
    private sealed record Budget(double Width, double FontSize);
}
