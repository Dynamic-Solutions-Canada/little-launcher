using LittleLauncher.Models;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace LittleLauncher.Controls;

/// <summary>
/// Validates launcher moves against the current derived columns before changing either collection.
/// This policy has no window dependencies so identity, nesting and persistence can be regression tested.
/// </summary>
internal static class LauncherItemReorder
{
    /// <summary>
    /// Checks that both collections still belong to the current view and that the source still
    /// contains the exact dragged object. Real groups may move only between top-level columns.
    /// Synthetic groups are display wrappers and cannot themselves move.
    /// </summary>
    internal static bool CanMove(
        LauncherItem item,
        ObservableCollection<LauncherItem> source,
        ObservableCollection<LauncherItem> target,
        IReadOnlyList<ObservableCollection<LauncherItem>> columns,
        ISet<LauncherItem> syntheticGroups)
    {
        if (item.IsColumnBreak || syntheticGroups.Contains(item) || !source.Contains(item))
            return false;

        bool sourceIsLive = false;
        bool targetIsLive = false;
        bool targetIsColumn = false;
        foreach (var column in columns)
        {
            sourceIsLive |= ReferenceEquals(source, column);
            if (ReferenceEquals(target, column))
            {
                targetIsLive = true;
                targetIsColumn = true;
            }

            foreach (var group in column.Where(entry => entry.IsGroup))
            {
                sourceIsLive |= ReferenceEquals(source, group.Children);
                targetIsLive |= ReferenceEquals(target, group.Children);
            }
        }

        if (!sourceIsLive || !targetIsLive || (item.IsGroup && !targetIsColumn))
            return false;

        // Do not duplicate an object if an intervening operation already moved it here.
        return ReferenceEquals(source, target) || !target.Contains(item);
    }

    /// <summary>
    /// Moves one existing object using a pre-removal insertion index. Returns true only when
    /// order or membership changed, so cancelled, stale and no-op drops never trigger persistence.
    /// </summary>
    internal static bool TryMove(
        LauncherItem item,
        ObservableCollection<LauncherItem> source,
        ObservableCollection<LauncherItem> target,
        int insertionIndex,
        IReadOnlyList<ObservableCollection<LauncherItem>> columns,
        ISet<LauncherItem> syntheticGroups)
    {
        if (!CanMove(item, source, target, columns, syntheticGroups))
            return false;

        int sourceIndex = source.IndexOf(item);
        int destinationIndex = System.Math.Clamp(insertionIndex, 0, target.Count);
        if (ReferenceEquals(source, target))
        {
            if (sourceIndex < destinationIndex)
                destinationIndex--;

            if (sourceIndex == destinationIndex)
                return false;

            source.Move(sourceIndex, destinationIndex);
        }
        else
        {
            source.RemoveAt(sourceIndex);
            target.Insert(destinationIndex, item);
        }

        return true;
    }

    /// <summary>
    /// Converts the current columns to persisted items without copying item objects or saving
    /// synthetic wrappers. Empty columns are retained as column-break sentinels.
    /// </summary>
    internal static IEnumerable<LauncherItem> Flatten(
        IEnumerable<ObservableCollection<LauncherItem>> columns,
        ISet<LauncherItem> syntheticGroups)
    {
        bool firstColumn = true;
        foreach (var column in columns)
        {
            if (!firstColumn)
                yield return LauncherItem.CreateColumnBreak();
            firstColumn = false;

            foreach (var item in column)
            {
                if (syntheticGroups.Contains(item))
                {
                    foreach (var child in item.Children)
                        yield return child;
                }
                else
                {
                    yield return item;
                }
            }
        }
    }
}
