using System.Windows;
using System.Windows.Controls;
using AcEvoFfbTuner.Help;

namespace AcEvoFfbTuner.Converters;

/// <summary>
/// Picks the XAML template used to render each HelpBlock type inside the guide.
/// </summary>
public sealed class HelpBlockTemplateSelector : DataTemplateSelector
{
    public DataTemplate? ParagraphTemplate { get; set; }
    public DataTemplate? BulletsTemplate { get; set; }
    public DataTemplate? NoteTemplate { get; set; }
    public DataTemplate? SliderTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        return item switch
        {
            HelpParagraph => ParagraphTemplate,
            HelpBullets => BulletsTemplate,
            HelpNote => NoteTemplate,
            HelpSliderRow => SliderTemplate,
            _ => null
        };
    }
}
