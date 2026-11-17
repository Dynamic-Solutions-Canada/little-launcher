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
void Check(bool value) { if (!value) { throw new Exception("Assertion failed"); } }
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
    await Test("interface language matches Persistent's rules and falls back to English", () =>
    {
        using var _ = new LanguageScope();
        string Resolve(params string[] preferences) => LittleLauncher.Localization.SupportedLanguages.Resolve(preferences);
        Check(Resolve("fr-CA") == "fr-CA" && Resolve("fr-BE") == "fr-FR" && Resolve("en-GB") == "en-US");
        Check(Resolve("it-IT", "es-MX") == "es-ES" && Resolve("it-IT") == "en-US");
        Check(Resolve("zh-TW") == "en-US" && Resolve("zh-SG") == "zh-CN" && Resolve("zh-Hant-CN", "ja") == "ja-JP");
        Check(Resolve("ja-JP-u-ca-japanese") == "ja-JP" && Resolve("en-Latn-CA") == "en-CA");

        // Override wins; an unknown override means Match device.
        LittleLauncher.Localization.UiText.Configure(() => "ja-jp", () => ["fr-FR"]);
        Check(LittleLauncher.Localization.UiText.Locale == "ja-JP");
        LittleLauncher.Localization.UiText.Configure(() => "it-IT", () => ["fr-CA"]);
        Check(LittleLauncher.Localization.UiText.Locale == "fr-CA");

        // Embedded English catalog, key fallback and single-pass placeholders.
        LittleLauncher.Localization.UiText.Configure(() => "en-US");
        Check(LittleLauncher.Localization.UiText.Get("common.ok") == "OK");
        Check(LittleLauncher.Localization.UiText.Get("no.such.key") == "no.such.key");
        Check(LittleLauncher.Localization.UiText.Format("system.language.matchDevice", ("language", "{language}")) == "Match device ({language})");

        // Every other catalog has exactly the English keys once translations exist.
        var english = LittleLauncher.Localization.UiText.Catalog("en-US");
        foreach (string catalog in LittleLauncher.Localization.SupportedLanguages.Catalogs.Values.Distinct())
        {
            var translated = LittleLauncher.Localization.UiText.Catalog(catalog);
            Check(translated.Count == english.Count && english.Keys.All(translated.ContainsKey));
        }
        return Task.CompletedTask;
    });
    await Test("no sync destination is switched on until one is set up", () =>
    {
        var options = new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault };

        // A fresh install: SyncProvider is SFTP by CLR default, but nothing is configured.
        var fresh = new LittleLauncher.ViewModels.UserSettings();
        fresh.CompleteInitialization();
        Check(fresh.EnabledSyncProviders is { Count: 0 });
        Check(!fresh.IsSyncProviderEnabled(LittleLauncher.Models.SyncProviders.Sftp));

        // A pre-multi-destination file that really used SFTP keeps it.
        var legacy = JsonSerializer.Deserialize<LittleLauncher.ViewModels.UserSettings>("{\"SftpHost\":\"nas.example\"}", options)!;
        legacy.CompleteInitialization();
        Check(legacy.IsSyncProviderEnabled(LittleLauncher.Models.SyncProviders.Sftp));

        // A legacy folder choice without a folder starts empty; a cloud choice is kept.
        var folder = JsonSerializer.Deserialize<LittleLauncher.ViewModels.UserSettings>("{\"SyncProvider\":4}", options)!;
        folder.CompleteInitialization();
        Check(folder.EnabledSyncProviders is { Count: 0 });
        var cloud = JsonSerializer.Deserialize<LittleLauncher.ViewModels.UserSettings>("{\"SyncProvider\":1}", options)!;
        cloud.CompleteInitialization();
        Check(cloud.IsSyncProviderEnabled(LittleLauncher.Models.SyncProviders.OneDrive));

        // A file already migrated by an earlier build with SFTP seeded but no host drops it,
        // keeping any other destination.
        var seeded = JsonSerializer.Deserialize<LittleLauncher.ViewModels.UserSettings>("{\"EnabledSyncProviders\":[0,2]}", options)!;
        seeded.CompleteInitialization();
        Check(seeded.EnabledSyncProviders!.SequenceEqual([LittleLauncher.Models.SyncProviders.GoogleDrive]));

        // Deliberately everything off stays off and serializes as empty, not absent.
        string saved = JsonSerializer.Serialize(fresh, options);
        Check(JsonDocument.Parse(saved).RootElement.GetProperty("EnabledSyncProviders").GetArrayLength() == 0);
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
        var source = new Launcher
        {
            Id = "platform",
            Platform = "linux",
            TrayIconMode = "Glyph:#FF0000:A",
            TrayIconPreviewMode = "Glyph:#FF0000:A",
            TrayIconPreviewData = Convert.ToBase64String(png)
        };
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
        byte[] oversized = (byte[])png.Clone(); oversized[16] = 0x7f;
        bool rejected = false; try { PortableLauncherIcon.DecodePreview(Convert.ToBase64String(oversized)); } catch (InvalidDataException) { rejected = true; }
        Check(rejected);
    });
    Console.WriteLine($"{passed} production sync/model cases passed");
}
finally { Directory.Delete(root, recursive: true); }

/// <summary>Restores Match device with the process culture after a test changes the language.</summary>
sealed class LanguageScope : IDisposable
{
    public void Dispose() => LittleLauncher.Localization.UiText.Configure(() => null, () => [System.Globalization.CultureInfo.CurrentUICulture.Name]);
}
