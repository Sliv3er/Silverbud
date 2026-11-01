using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Silverbud.Controls;

/// <summary>A titled settings line: label and description on the left, the control on the right.</summary>
public sealed class SettingRow : ContentControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingRow), new PropertyMetadata(""));

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingRow), new PropertyMetadata(""));

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }

    /// <summary>Switches and sliders have no text of their own; name them after the row so screen readers announce them.</summary>
    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        if (newContent is ToggleButton or RangeBase && newContent is DependencyObject control &&
            string.IsNullOrEmpty(AutomationProperties.GetName(control)))
            AutomationProperties.SetName(control, Title);
    }
}
