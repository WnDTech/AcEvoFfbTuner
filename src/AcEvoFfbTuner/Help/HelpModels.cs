using System.Collections.Generic;

namespace AcEvoFfbTuner.Help;

/// <summary>
/// A single help article shown in the Help guide. Articles are data-driven so the
/// guide can be extended or reworded without touching the view.
/// </summary>
public sealed class HelpArticle
{
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string[] Keywords { get; set; } = System.Array.Empty<string>();
    public List<HelpSection> Sections { get; } = new();

    /// <summary>Pre-computed lowercase search index (title, headings, bodies, slider names).</summary>
    public string SearchText { get; set; } = "";
}

public sealed class HelpSection
{
    public string Heading { get; set; } = "";
    public List<HelpBlock> Blocks { get; } = new();
}

/// <summary>Base type for one piece of article content.</summary>
public abstract record HelpBlock;

/// <summary>A plain explanatory paragraph.</summary>
public sealed record HelpParagraph(string Text) : HelpBlock;

/// <summary>A bulleted list.</summary>
public sealed record HelpBullets(IReadOnlyList<string> Items) : HelpBlock;

/// <summary>A highlighted callout box (tip, warning, or "good to know").</summary>
public sealed record HelpNote(string Title, string Text) : HelpBlock;

/// <summary>
/// One adjustable slider/control row: its name, range, and a plain-language
/// description of the "feel" it produces.
/// </summary>
public sealed record HelpSliderRow(string Name, string Range, string Feel) : HelpBlock;
