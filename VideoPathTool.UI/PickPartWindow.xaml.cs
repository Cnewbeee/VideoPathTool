using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using VideoPathTool.Core;

namespace VideoPathTool.UI;

/// <summary>
/// Lets the user pick which page of a bilibili collection to analyse.
///
/// Shown only when the pasted link has no <c>?p=</c> and the collection really
/// has more than one page, so a plain single video still downloads in one click.
/// </summary>
public partial class PickPartWindow : Window
{
    /// <summary>One row of the list.</summary>
    public sealed class Row
    {
        public int Page { get; init; }
        public string PageLabel => "P" + Page;
        public string Title { get; init; } = string.Empty;
        public string DurationText { get; init; } = string.Empty;
    }

    /// <summary>The page the user confirmed (1-based); 0 when cancelled.</summary>
    public int SelectedPage { get; private set; }

    public PickPartWindow(string header, IReadOnlyList<BiliPart> parts, int? preselect = null)
    {
        InitializeComponent();

        TxtHeader.Text = header;
        var rows = parts
            .OrderBy(p => p.Page)
            .Select(p => new Row
            {
                Page = p.Page,
                Title = string.IsNullOrWhiteSpace(p.Title) ? "(无标题)" : p.Title,
                DurationText = p.DurationSec > 0 ? FormatDuration(p.DurationSec) : ""
            })
            .ToList();

        PartGrid.ItemsSource = rows;
        if (rows.Count > 0)
        {
            var index = preselect is > 0
                ? rows.FindIndex(r => r.Page == preselect.Value)
                : 0;
            PartGrid.SelectedIndex = index >= 0 ? index : 0;
            PartGrid.ScrollIntoView(PartGrid.SelectedItem);
        }
    }

    private static string FormatDuration(double seconds)
    {
        var t = System.TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";
    }

    private void OnOkClick(object sender, RoutedEventArgs e) => Accept();

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        SelectedPage = 0;
        DialogResult = false;
    }

    private void OnGridDoubleClick(object sender, MouseButtonEventArgs e) => Accept();

    private void Accept()
    {
        if (PartGrid.SelectedItem is not Row row)
        {
            return;
        }
        SelectedPage = row.Page;
        DialogResult = true;
    }
}
