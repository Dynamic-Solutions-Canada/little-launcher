using System.Collections.ObjectModel;
using System.Text.Json;
using LittleLauncher.Controls;
using LittleLauncher.Models;

// Dependency-free runner, apart from the app's existing observable-model generator.
// Each case exercises production move/flatten policy with the real LauncherItem model.
var cases = new List<(string Name, Action Run)>();
void Case(string name, Action action) => cases.Add((name, action));

Case("same-column downward move adjusts pre-removal index", () =>
{
    var f = new Fixture();
    Check(f.Move(f.A, f.Left, f.Left, 3));
    Order(f.Left, f.B, f.Group, f.A);
});
Case("same-column upward move preserves object identity", () =>
{
    var f = new Fixture();
    Check(f.Move(f.Group, f.Left, f.Left, 0));
    Order(f.Left, f.Group, f.A, f.B);
    Order(f.Group.Children, f.Child, f.OtherChild);
});
Case("whole group crosses columns with its children intact", () =>
{
    var f = new Fixture();
    Check(f.Move(f.Group, f.Left, f.Right, 0));
    Order(f.Right, f.Group, f.C);
    Order(f.Group.Children, f.Child, f.OtherChild);
});
Case("individual item crosses columns", () =>
{
    var f = new Fixture();
    Check(f.Move(f.A, f.Left, f.Right, 1));
    Order(f.Right, f.C, f.A);
    Order(f.Left, f.B, f.Group);
});
Case("child reorders within its group", () =>
{
    var f = new Fixture();
    Check(f.Move(f.Child, f.Group.Children, f.Group.Children, 2));
    Order(f.Group.Children, f.OtherChild, f.Child);
});
Case("top-level item moves into a group", () =>
{
    var f = new Fixture();
    Check(f.Move(f.A, f.Left, f.Group.Children, 1));
    Order(f.Group.Children, f.Child, f.A, f.OtherChild);
});
Case("child moves out to the same column", () =>
{
    var f = new Fixture();
    Check(f.Move(f.Child, f.Group.Children, f.Left, 2));
    Order(f.Left, f.A, f.B, f.Child, f.Group);
    Order(f.Group.Children, f.OtherChild);
});
Case("child moves out across columns", () =>
{
    var f = new Fixture();
    Check(f.Move(f.Child, f.Group.Children, f.Right, 0));
    Order(f.Right, f.Child, f.C);
});
Case("child moves between groups", () =>
{
    var f = new Fixture();
    var other = LauncherItem.CreateGroup("Other");
    f.Right.Add(other);
    Check(f.Move(f.Child, f.Group.Children, other.Children, 0));
    Order(other.Children, f.Child);
});
Case("empty column accepts a group", () =>
{
    var f = new Fixture();
    f.Right.Clear();
    Check(f.Move(f.Group, f.Left, f.Right, 0));
    Order(f.Right, f.Group);
});
Case("empty group accepts an item", () =>
{
    var f = new Fixture();
    f.Group.Children.Clear();
    Check(f.Move(f.A, f.Left, f.Group.Children, 0));
    Order(f.Group.Children, f.A);
});
Case("group cannot move into its own children", () =>
{
    var f = new Fixture();
    Check(!f.Move(f.Group, f.Left, f.Group.Children, 0));
    Order(f.Left, f.A, f.B, f.Group);
});
Case("group cannot nest in another group", () =>
{
    var f = new Fixture();
    var other = LauncherItem.CreateGroup("Other");
    f.Right.Add(other);
    Check(!f.Move(f.Group, f.Left, other.Children, 0));
    Order(other.Children);
});
Case("synthetic wrapper cannot itself move", () =>
{
    var f = new Fixture();
    f.Synthetic.Add(f.Group);
    Check(!f.Move(f.Group, f.Left, f.Right, 0));
});
Case("child can move out of synthetic wrapper", () =>
{
    var f = new Fixture();
    f.Synthetic.Add(f.Group);
    Check(f.Move(f.Child, f.Group.Children, f.Right, 1));
    Order(f.Right, f.C, f.Child);
});
Case("group cannot enter synthetic wrapper", () =>
{
    var f = new Fixture();
    var wrapper = LauncherItem.CreateGroup("");
    f.Right.Add(wrapper);
    f.Synthetic.Add(wrapper);
    Check(!f.Move(f.Group, f.Left, wrapper.Children, 0));
});
Case("column sentinel cannot be moved", () =>
{
    var f = new Fixture();
    var sentinel = LauncherItem.CreateColumnBreak();
    f.Left.Add(sentinel);
    Check(!f.Move(sentinel, f.Left, f.Right, 0));
});
Case("removed source is never resurrected", () =>
{
    var f = new Fixture();
    f.Left.Remove(f.A);
    Check(!f.Move(f.A, f.Left, f.Right, 0));
    Order(f.Right, f.C);
});
Case("equal-valued replacement is not the dragged object", () =>
{
    var f = new Fixture();
    f.Left[0] = new LauncherItem(f.A.Name, f.A.Path, f.A.IconGlyph);
    Check(!f.Move(f.A, f.Left, f.Right, 0));
});
Case("discarded source column cannot commit after rebuild", () =>
{
    var f = new Fixture();
    var discarded = f.Left;
    f.Columns[0] = new ObservableCollection<LauncherItem>(discarded);
    Check(!f.Move(f.A, discarded, f.Right, 0));
});
Case("discarded target column cannot accept a drop", () =>
{
    var f = new Fixture();
    var discarded = f.Right;
    f.Columns[1] = new ObservableCollection<LauncherItem>(discarded);
    Check(!f.Move(f.A, f.Left, discarded, 0));
});
Case("removed group invalidates its child source", () =>
{
    var f = new Fixture();
    f.Left.Remove(f.Group);
    Check(!f.Move(f.Child, f.Group.Children, f.Right, 0));
});
Case("replaced children invalidate their old target", () =>
{
    var f = new Fixture();
    var discarded = f.Group.Children;
    f.Group.Children = new();
    Check(!f.Move(f.A, f.Left, discarded, 0));
});
Case("intervening destination membership cannot duplicate an item", () =>
{
    var f = new Fixture();
    f.Right.Add(f.A);
    Check(!f.Move(f.A, f.Left, f.Right, 0));
    Order(f.Left, f.A, f.B, f.Group);
});
Case("drop before itself has no save-worthy change", () =>
{
    var f = new Fixture();
    int events = 0;
    f.Left.CollectionChanged += (_, _) => events++;
    Check(!f.Move(f.B, f.Left, f.Left, 1));
    Check(events == 0);
});
Case("drop after itself has no save-worthy change", () =>
{
    var f = new Fixture();
    Check(!f.Move(f.B, f.Left, f.Left, 2));
    Order(f.Left, f.A, f.B, f.Group);
});
Case("negative insertion index clamps safely", () =>
{
    var f = new Fixture();
    Check(f.Move(f.C, f.Right, f.Left, -100));
    Order(f.Left, f.C, f.A, f.B, f.Group);
});
Case("oversized insertion index clamps safely", () =>
{
    var f = new Fixture();
    Check(f.Move(f.A, f.Left, f.Right, 100));
    Order(f.Right, f.C, f.A);
});
Case("validation alone leaves cancellation mutation-free", () =>
{
    var f = new Fixture();
    Check(LauncherItemReorder.CanMove(f.A, f.Left, f.Right, f.Columns, f.Synthetic));
    Order(f.Left, f.A, f.B, f.Group);
    Order(f.Right, f.C);
});
Case("flatten strips wrappers and preserves empty columns and references", () =>
{
    var f = new Fixture();
    f.Synthetic.Add(f.Group);
    f.Right.Clear();
    f.Columns.Add(new());
    var flat = LauncherItemReorder.Flatten(f.Columns, f.Synthetic).ToArray();
    Order(flat.Take(4), f.A, f.B, f.Child, f.OtherChild);
    Check(flat.Length == 6 && flat[4].IsColumnBreak && flat[5].IsColumnBreak);
    Check(!flat.Contains(f.Group));
});
Case("move then save/reload retains group membership and ordering", () =>
{
    var f = new Fixture();
    Check(f.Move(f.A, f.Left, f.Group.Children, 1));
    Check(f.Move(f.Group, f.Left, f.Right, 0));
    var flat = LauncherItemReorder.Flatten(f.Columns, f.Synthetic).ToArray();
    var saved = JsonSerializer.Serialize(flat);
    var reloaded = JsonSerializer.Deserialize<LauncherItem[]>(saved)!;
    Check(reloaded.Select(i => i.Name).SequenceEqual(new[] { "B", "", "Group", "C" }));
    Check(reloaded[1].IsColumnBreak);
    Check(reloaded[2].Children.Select(i => i.Name).SequenceEqual(new[] { "Child", "A", "Other child" }));
    Check(!saved.Contains("IsExpanded"));
});

Case("group outer bands distinguish before, inside and after at exact thresholds", () =>
{
    Check(LauncherDropGeometry.GetGroupZone(50, 0, 100, 100) == LauncherDropGeometry.GroupZone.Before);
    Check(LauncherDropGeometry.GetGroupZone(50, 7.99, 100, 100) == LauncherDropGeometry.GroupZone.Before);
    Check(LauncherDropGeometry.GetGroupZone(50, 8, 100, 100) == LauncherDropGeometry.GroupZone.Inside);
    Check(LauncherDropGeometry.GetGroupZone(50, 91.99, 100, 100) == LauncherDropGeometry.GroupZone.Inside);
    Check(LauncherDropGeometry.GetGroupZone(50, 92, 100, 100) == LauncherDropGeometry.GroupZone.After);
    Check(LauncherDropGeometry.GetGroupZone(50, 99.99, 100, 100) == LauncherDropGeometry.GroupZone.After);
});
Case("short groups retain an interior instead of overlapping sibling zones", () =>
{
    Check(LauncherDropGeometry.GetGroupZone(5, 1, 10, 8) == LauncherDropGeometry.GroupZone.Before);
    Check(LauncherDropGeometry.GetGroupZone(5, 2, 10, 8) == LauncherDropGeometry.GroupZone.Inside);
    Check(LauncherDropGeometry.GetGroupZone(5, 5.99, 10, 8) == LauncherDropGeometry.GroupZone.Inside);
    Check(LauncherDropGeometry.GetGroupZone(5, 6, 10, 8) == LauncherDropGeometry.GroupZone.After);
});
Case("group zones reject points outside the full container", () =>
{
    foreach (var point in new[] { (-1d, 50d), (100d, 50d), (50d, -1d), (50d, 100d) })
        Check(LauncherDropGeometry.GetGroupZone(point.Item1, point.Item2, 100, 100) == LauncherDropGeometry.GroupZone.Outside);
});
Case("detached or non-finite group geometry never becomes a target", () =>
{
    Check(LauncherDropGeometry.GetGroupZone(0, 0, 0, 100) == LauncherDropGeometry.GroupZone.Outside);
    Check(LauncherDropGeometry.GetGroupZone(0, 0, 100, 0) == LauncherDropGeometry.GroupZone.Outside);
    Check(LauncherDropGeometry.GetGroupZone(double.NaN, 0, 100, 100) == LauncherDropGeometry.GroupZone.Outside);
    Check(LauncherDropGeometry.GetGroupZone(0, double.PositiveInfinity, 100, 100) == LauncherDropGeometry.GroupZone.Outside);
});
Case("child appended inside and child placed after group persist different topology", () =>
{
    var inside = new Fixture();
    Check(inside.Move(inside.A, inside.Left, inside.Group.Children, inside.Group.Children.Count));
    var after = new Fixture();
    Check(after.Move(after.A, after.Left, after.Left, after.Left.IndexOf(after.Group) + 1));
    Order(inside.Left, inside.B, inside.Group);
    Order(inside.Group.Children, inside.Child, inside.OtherChild, inside.A);
    Order(after.Left, after.B, after.Group, after.A);
    Order(after.Group.Children, after.Child, after.OtherChild);
    var insideSaved = JsonSerializer.Serialize(LauncherItemReorder.Flatten(inside.Columns, inside.Synthetic));
    var afterSaved = JsonSerializer.Serialize(LauncherItemReorder.Flatten(after.Columns, after.Synthetic));
    Check(insideSaved != afterSaved);
    var reloaded = JsonSerializer.Deserialize<LauncherItem[]>(afterSaved)!;
    Check(reloaded[1].IsGroup && reloaded[2].Name == "A");
});
Case("child extracted after its own group retains the original object", () =>
{
    var f = new Fixture();
    Check(f.Move(f.Child, f.Group.Children, f.Left, f.Left.IndexOf(f.Group) + 1));
    Order(f.Left, f.A, f.B, f.Group, f.Child);
    Order(f.Group.Children, f.OtherChild);
});

Case("end marker stays inset within list, icon, compact and tiny last children", () =>
{
    foreach (var size in new[] { (106d, 32d), (80d, 84d), (40d, 40d), (4d, 6d) })
    {
        var rect = LauncherDropGeometry.GetEndMarker(size.Item1, size.Item2)
            ?? throw new InvalidOperationException("A realised last child must have an end marker");
        Check(rect.Left > 0 && rect.Top > 0 && rect.Width > 0 && rect.Height > 0);
        Check(rect.Left + rect.Width < size.Item1);
        Check(rect.Top + rect.Height < size.Item2);
    }
});
Case("end marker rejects unrealised and non-finite child dimensions", () =>
{
    Check(LauncherDropGeometry.GetEndMarker(0, 32) == null);
    Check(LauncherDropGeometry.GetEndMarker(80, -1) == null);
    Check(LauncherDropGeometry.GetEndMarker(double.NaN, 40) == null);
    Check(LauncherDropGeometry.GetEndMarker(80, double.PositiveInfinity) == null);
});

int failures = 0;
foreach (var test in cases)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}");
    }
}
Console.WriteLine($"{cases.Count - failures}/{cases.Count} launcher reorder cases passed");
return failures == 0 ? 0 : 1;

static void Check(bool condition)
{
    if (!condition)
        throw new InvalidOperationException("Assertion failed");
}

/// <summary>Compares references, ensuring moves do not silently replace observable objects.</summary>
static void Order(IEnumerable<LauncherItem> actual, params LauncherItem[] expected)
{
    var items = actual.ToArray();
    Check(items.Length == expected.Length);
    for (int index = 0; index < items.Length; index++)
        Check(ReferenceEquals(items[index], expected[index]));
}

sealed class Fixture
{
    internal readonly LauncherItem A = new("A", "a.exe", "");
    internal readonly LauncherItem B = new("B", "b.exe", "");
    internal readonly LauncherItem C = new("C", "c.exe", "");
    internal readonly LauncherItem Child = new("Child", "child.exe", "");
    internal readonly LauncherItem OtherChild = new("Other child", "other.exe", "");
    internal readonly LauncherItem Group = LauncherItem.CreateGroup("Group");
    internal readonly ObservableCollection<LauncherItem> Left;
    internal readonly ObservableCollection<LauncherItem> Right;
    internal readonly List<ObservableCollection<LauncherItem>> Columns;
    internal readonly HashSet<LauncherItem> Synthetic = [];

    internal Fixture()
    {
        Group.Children.Add(Child);
        Group.Children.Add(OtherChild);
        Left = [A, B, Group];
        Right = [C];
        Columns = [Left, Right];
    }

    internal bool Move(LauncherItem item, ObservableCollection<LauncherItem> source,
        ObservableCollection<LauncherItem> target, int index) =>
        LauncherItemReorder.TryMove(item, source, target, index, Columns, Synthetic);
}
