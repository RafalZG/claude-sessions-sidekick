using System.Windows;

namespace ClaudeSessionsSidekick.Services;

/// <summary>
/// Attached property for tagging DataGridColumn with a stable, code-only
/// identifier. DataGridColumn doesn't inherit from FrameworkElement so the
/// built-in Tag property isn't available; using header text as a key was
/// fragile against trailing whitespace, header restyling, or future i18n.
/// Usage: <c>local:DataGridColumnHelper.Key="project"</c> in XAML.
/// </summary>
public static class DataGridColumnHelper
{
    public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
        "Key",
        typeof(string),
        typeof(DataGridColumnHelper),
        new PropertyMetadata(null));

    public static void SetKey(DependencyObject element, string value) =>
        element.SetValue(KeyProperty, value);

    public static string? GetKey(DependencyObject element) =>
        (string?)element.GetValue(KeyProperty);
}
