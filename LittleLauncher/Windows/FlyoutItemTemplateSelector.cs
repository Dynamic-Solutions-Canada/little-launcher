using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using LittleLauncher.Models;
using System.Collections.Generic;

namespace LittleLauncher.Windows;

public sealed class FlyoutItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? GroupTemplate { get; set; }
    public DataTemplate? ItemTemplate { get; set; }
    public DataTemplate? SyntheticGroupTemplate { get; set; }
    public ICollection<LauncherItem>? SyntheticGroups { get; set; }

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
    {
        if (item is LauncherItem { IsGroup: true } group)
        {
            if (SyntheticGroupTemplate != null && SyntheticGroups?.Contains(group) == true)
            {
                return SyntheticGroupTemplate;
            }

            return GroupTemplate!;
        }

        return ItemTemplate!;
    }
}
