using System.Collections.ObjectModel;
using System.Text.Json;
using LittleLauncher;
using LittleLauncher.Classes.Settings;
using LittleLauncher.Models;
using LittleLauncher.Services;

string root = Path.Combine(Path.GetTempPath(), "little-launcher-sync-model-" + Guid.NewGuid());
Directory.CreateDirectory(root);
MainWindow.DirectoryPath = Path.Combine(root, "second");
byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLttAAAAABJRU5ErkJggg==");
int passed = 0;
void Check(bool value) { if (!value) throw new Exception("Assertion failed"); }
async Task Test(string name, Func<Task> body) { await body(); passed++; Console.WriteLine("PASS " + name); }
try
{
    if (args.Length == 3 && args[0] == "--roundtrip")
    {
        using var input = File.OpenRead(args[1]);
        var (models, _, _) = LauncherPayload.Deserialize(input);
        await LauncherPayload.ApplyAsync(models!);
        using var output = LauncherPayload.Serialize(SettingsManager.Current.Launchers);
        File.WriteAllBytes(args[2], output.ToArray());
        Console.WriteLine("Production C# payload round-trip complete");
        return;
    }
    await Test("login startup defaults on and explicit off survives actual model JSON", () =>
    {
        var options = new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault };
        var settings = new LittleLauncher.ViewModels.UserSettings();
        Check(settings.Startup);
        settings.Startup = false;
        string saved = JsonSerializer.Serialize(settings, options);
        Check(JsonDocument.Parse(saved).RootElement.GetProperty("Startup").GetBoolean() == false);
        Check(!JsonSerializer.Deserialize<LittleLauncher.ViewModels.UserSettings>(saved, options)!.Startup);
        return Task.CompletedTask;
    });
    await Test("custom image travels with actual envelope and restores at a different local path", async () =>
    {
        string selected = Path.Combine(root, "chosen.png");
        File.WriteAllBytes(selected, png);
        var launcher = new Launcher { Id = "stable", Name = "Mine", TrayIconMode = "Custom", CustomTrayIconPath = selected };
        launcher.Items.Add(new LauncherItem { Path = @"C:\Windows\tool.exe", SupportedOperatingSystems = ["windows"] });
        using var wire = LauncherPayload.Serialize(new ObservableCollection<Launcher> { launcher });
        Check(string.IsNullOrEmpty(launcher.CustomTrayIconData)); // No live model notifications during upload.
        File.Delete(selected);
        var (launchers, _, _) = LauncherPayload.Deserialize(wire);
        Check(launchers![0].CustomTrayIconData.Length > 0);
        await LauncherPayload.ApplyAsync(launchers);
        var received = SettingsManager.Current.Launchers.Single();
        Check(received.CustomTrayIconPath != selected);
        Check(File.ReadAllBytes(received.CustomTrayIconPath).SequenceEqual(png));
        Check(received.Items[0].SupportedOperatingSystems!.SequenceEqual(["windows"]));
        using var back = LauncherPayload.Serialize(SettingsManager.Current.Launchers);
        Check(LauncherPayload.Deserialize(back).Launchers![0].CustomTrayIconData == Convert.ToBase64String(png));
    });
    await Test("a different web-launcher image survives merge and a return upload", async () =>
    {
        var existing = SettingsManager.Current.Launchers.Single();
        string oldPath = existing.CustomTrayIconPath;
        byte[] replacement = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGNgYPj/HwADAgH/5ncLrgAAAABJRU5ErkJggg==");
        var downloaded = new Launcher { Id = existing.Id, Kind = LauncherKinds.Web, Name = "Changed", TrayIconMode = "Custom", CustomTrayIconPath = "/foreign/missing.png", CustomTrayIconData = Convert.ToBase64String(replacement) };
        await LauncherPayload.ApplyAsync([downloaded]);
        Check(ReferenceEquals(existing, SettingsManager.Current.Launchers.Single()));
        Check(existing.IsWebLauncher && existing.Name == "Changed" && File.Exists(existing.CustomTrayIconPath));
        Check(existing.CustomTrayIconPath != oldPath);
        Check(File.ReadAllBytes(existing.CustomTrayIconPath).SequenceEqual(replacement));
        Check(existing.CustomTrayIconData == Convert.ToBase64String(replacement));
        using var returned = LauncherPayload.Serialize(SettingsManager.Current.Launchers);
        Check(LauncherPayload.Deserialize(returned).Launchers!.Single().CustomTrayIconData == Convert.ToBase64String(replacement));
    });
    await Test("an older writer with a missing path does not erase the available local custom image", async () =>
    {
        var existing = SettingsManager.Current.Launchers.Single();
        string path = existing.CustomTrayIconPath;
        await LauncherPayload.ApplyAsync([new Launcher { Id = existing.Id, TrayIconMode = "Custom", CustomTrayIconPath = @"Z:\missing.png" }]);
        Check(existing.CustomTrayIconPath == path);
        Check(File.ReadAllBytes(path).SequenceEqual(Convert.FromBase64String(existing.CustomTrayIconData)));
    });
    await Test("malformed portable content fails before the live merge", async () =>
    {
        var existing = SettingsManager.Current.Launchers.Single();
        string name = existing.Name;
        try
        {
            await LauncherPayload.ApplyAsync([new Launcher { Id = existing.Id, Name = "BAD", TrayIconMode = "Custom", CustomTrayIconData = "%%%%" }]);
            throw new Exception("Malformed content was accepted");
        }
        catch (FormatException) { }
        Check(existing.Name == name);
    });
    await Test("a local custom path replacement clears obsolete embedded image data", () =>
    {
        var launcher = new Launcher { CustomTrayIconPath = "old", CustomTrayIconData = Convert.ToBase64String(png) };
        launcher.CustomTrayIconPath = "new";
        Check(launcher.CustomTrayIconData == "");
        return Task.CompletedTask;
    });
    await Test("sessions, pin identity and monitor position never travel on new or existing launchers", async () =>
    {
        var existing = SettingsManager.Current.Launchers.Single();
        existing.WebSessionTabs = ["https://local.example"];
        existing.WebFlyoutPosition = "1,2";
        existing.PinAumid = "local-pin";
        using var wire = LauncherPayload.Serialize(SettingsManager.Current.Launchers);
        var downloaded = LauncherPayload.Deserialize(wire).Launchers!.Single();
        Check(downloaded.WebSessionTabs == null && downloaded.PinAumid == "" && downloaded.WebFlyoutPosition == "");
        downloaded.WebSessionTabs = ["https://foreign.example"];
        downloaded.WebFlyoutPosition = "90,90";
        downloaded.PinAumid = "foreign-pin";
        await LauncherPayload.ApplyAsync([downloaded]);
        Check(existing.WebSessionTabs.Single() == "https://local.example" && existing.PinAumid == "local-pin" && existing.WebFlyoutPosition == "1,2");
        downloaded = new Launcher { Id = "new", WebSessionTabs = ["https://foreign.example"], PinAumid = "foreign-pin" };
        await LauncherPayload.ApplyAsync([JsonSerializer.Deserialize<Launcher>(JsonSerializer.Serialize(existing))!, downloaded]);
        Check(downloaded.WebSessionTabs == null && downloaded.PinAumid == "");
    });
    await Test("image content survives arbitrary JSON property order while local path edits invalidate it", () =>
    {
        string json = "{\"CustomTrayIconData\":\"" + Convert.ToBase64String(png) + "\",\"CustomTrayIconPath\":\"foreign.png\",\"TrayIconMode\":\"Custom\"}";
        var model = JsonSerializer.Deserialize<Launcher>(json)!;
        Check(model.CustomTrayIconData == Convert.ToBase64String(png));
        model.CustomTrayIconPath = "locally-chosen.png";
        Check(model.CustomTrayIconData == "");
        return Task.CompletedTask;
    });
    await Test("whole-launcher OS ownership and font preview survive real payload and merge", async () =>
    {
        var legacy = JsonSerializer.Deserialize<Launcher>("{\"Id\":\"legacy\"}")!;
        Check(legacy.Platform == "windows" && legacy.IsSupportedOnThisOperatingSystem);
        var source = new Launcher { Id = "platform", Platform = "linux", TrayIconMode = "Glyph:#FF0000:A",
            TrayIconPreviewMode = "Glyph:#FF0000:A", TrayIconPreviewData = Convert.ToBase64String(png) };
        Check(!source.IsSupportedOnThisOperatingSystem);
        using var stream = LauncherPayload.Serialize(new ObservableCollection<Launcher> { source });
        var (downloaded, _, _) = LauncherPayload.Deserialize(stream);
        Check(downloaded![0].Platform == "linux" && downloaded[0].TrayIconPreviewData == source.TrayIconPreviewData);
        Check(source.TrayIconPreviewData == Convert.ToBase64String(png));
        var web = new Launcher { Kind = LauncherKinds.Web, Platform = "linux" };
        Check(web.IsSupportedOnThisOperatingSystem);
        await LauncherPayload.ApplyAsync(downloaded);
        Check(SettingsManager.Current.Launchers.Single().Platform == "linux");
        var oldPeer = new Launcher { Id = source.Id, Platform = "linux", TrayIconMode = source.TrayIconMode };
        await LauncherPayload.ApplyAsync([oldPeer]);
        Check(SettingsManager.Current.Launchers.Single().TrayIconPreviewData == source.TrayIconPreviewData);
        byte[] oversized = (byte[])png.Clone();oversized[16]=0x7f;
        bool rejected=false;try { PortableLauncherIcon.DecodePreview(Convert.ToBase64String(oversized)); } catch(InvalidDataException) { rejected=true; }
        Check(rejected);
    });
    Console.WriteLine($"{passed} production sync/model cases passed");
}
finally { Directory.Delete(root, recursive: true); }
