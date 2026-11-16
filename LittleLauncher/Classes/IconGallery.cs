using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Text;
using LittleLauncher.Localization;
using LittleLauncher.Models;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using static LittleLauncher.Classes.NativeMethods;

namespace LittleLauncher.Classes;

/// <summary>
/// Provides a gallery-style icon chooser Flyout with tabs for Segoe Fluent Icons,
/// emojis, app color icons, selfh.st icons, and custom file browse.
/// </summary>
internal static class IconGallery
{
    /// <summary>Result of an icon gallery selection.</summary>
    /// <param name="Glyph">Unicode glyph character (Fluent icon or emoji). Null when an image was selected.</param>
    /// <param name="ImagePath">Local file path for image-based icons. Null when a glyph was selected.</param>
    /// <param name="PresetMode">TrayIconMode preset string (e.g. "Blue", "Composite"). Null when a glyph/image was selected.</param>
    /// <param name="Color">Optional hex color for the glyph (e.g. "#FF0000"). Null or empty for default theme color.</param>
    /// <param name="UsePageIcon">Restores a web launcher's managed automatic page icon when confirmed.</param>
    /// <param name="PageIconPreview">Non-mutating preview tied to the address being confirmed.</param>
    public record IconResult(string? Glyph, string? ImagePath, string? PresetMode = null,
        string? Color = null, bool UsePageIcon = false, PageIconPreview? PageIconPreview = null);

    // ── Tab indices ──────────────────────────────────────────
    private const int TabGlyphs = 0;
    private const int TabEmoji = 1;
    private const int TabAppIcons = 2;
    private const int TabSelfhSt = 3;

    // ── Layout constants ─────────────────────────────────────
    private const int MaxVisibleSelfhStIcons = 120;

    private static readonly HttpClient SelfhStHttp = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly SemaphoreSlim SelfhStCatalogLock = new(1, 1);

    /// <summary>
    /// Caps how many icon PNGs are in flight at once. The gallery shows up to
    /// <see cref="MaxVisibleSelfhStIcons"/> at a time, and firing that many requests at the CDN
    /// together makes every one of them slower than fetching them a handful at a time.
    /// </summary>
    private static readonly SemaphoreSlim SelfhStDownloadGate = new(8, 8);

    private static readonly JsonSerializerOptions SelfhStJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static IReadOnlyList<SelfhStIconEntry>? _selfhStCatalog;
    private static DateTimeOffset _selfhStCatalogFetchedAt;

    private const string SelfhStIndexUrl = "https://raw.githubusercontent.com/selfhst/icons/refs/heads/main/index.json";
    private const string SelfhStPngUrlPrefix = "https://cdn.jsdelivr.net/gh/selfhst/icons@main/png/";
    private static readonly Uri SelfhStIconsUri = new("https://selfh.st/icons/");
    private static readonly Uri SelfhStLicenseUri = new("https://github.com/selfhst/icons/blob/main/LICENSE");
    private static readonly TimeSpan SelfhStCatalogTtl = TimeSpan.FromHours(6);

    /// <summary>
    /// Creates the item icon gallery. Current selection is read again on every open when
    /// a provider is supplied, so a confirmed choice is also the next opening's selection.
    /// </summary>
    public static Flyout CreateFlyout(
        Action<IconResult> onSelected,
        Action onBrowseRequested,
        Action onReset,
        string? currentGlyph = null,
        string? currentColor = null,
        string? currentImagePath = null,
        IntPtr ownerHwnd = default,
        Func<IconResult>? getCurrentSelection = null)
    {
        return CreateGallery(onSelected, onBrowseRequested, onReset, ownerHwnd,
            getCurrentSelection ?? (() => new IconResult(currentGlyph, currentImagePath, Color: currentColor)),
            launcherPresets: false);
    }

    /// <summary>Creates the same gallery with launcher presets in place of item app icons.</summary>
    public static Flyout CreateLauncherIconFlyout(
        string currentMode,
        Action<IconResult> onSelected,
        Action onBrowseRequested,
        IntPtr ownerHwnd = default,
        Func<IconResult>? getCurrentSelection = null,
        Func<bool>? canUsePageIcon = null,
        Func<PageIconPreviewSource>? getPageIconPreview = null)
    {
        return CreateGallery(onSelected, onBrowseRequested, null, ownerHwnd,
            getCurrentSelection ?? (() => LauncherSelection(currentMode, null)),
            launcherPresets: true, canUsePageIcon: canUsePageIcon, getPageIconPreview: getPageIconPreview);
    }

    /// <summary>Converts a launcher's saved mode and image path to the gallery's shared selection.</summary>
    public static IconResult LauncherSelection(string mode, string? imagePath)
    {
        if (TrayIconModes.IsGlyphMode(mode))
        {
            return new IconResult(TrayIconModes.GetGlyphCharacter(mode), null,
                Color: TrayIconModes.GetGlyphColor(mode));
        }

        return mode == TrayIconModes.Custom
            ? new IconResult(null, imagePath)
            : new IconResult(null, null, mode);
    }

    /// <summary>
    /// Opens the image picker on its actual owning HWND. All icon Browse buttons accept
    /// the same formats; cancellation returns null without changing the saved selection.
    /// </summary>
    public static async Task<string?> PickImageAsync(IntPtr ownerHwnd)
    {
        var picker = new global::Windows.Storage.Pickers.FileOpenPicker();
        foreach (string extension in new[] { ".ico", ".png", ".jpg", ".jpeg", ".bmp" })
        {
            picker.FileTypeFilter.Add(extension);
        }

        WinRT.Interop.InitializeWithWindow.Initialize(picker, ownerHwnd);
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    /// <summary>
    /// Owns layout, navigation and pending selection for both galleries. Only icon content
    /// and the optional reset action differ. The footer stays outside the shrinking scroller.
    /// </summary>
    private static Flyout CreateGallery(
        Action<IconResult> onSelected, Action onBrowseRequested, Action? onReset,
        IntPtr ownerHwnd, Func<IconResult> getCurrentSelection, bool launcherPresets,
        Func<bool>? canUsePageIcon = null,
        Func<PageIconPreviewSource>? getPageIconPreview = null)
    {
        var flyout = new Flyout
        {
            // WinUI's show-time validator rejects Auto with E_INVALIDARG. Bottom also
            // allows the framework to choose another side when space is constrained.
            Placement = FlyoutPlacementMode.Bottom,
            ShouldConstrainToRootBounds = false
        };
        var presenterStyle = new Style(typeof(FlyoutPresenter));
        presenterStyle.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 0.0));
        presenterStyle.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 0.0));
        presenterStyle.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, 480.0));
        presenterStyle.Setters.Add(new Setter(FrameworkElement.MaxHeightProperty, 600.0));
        presenterStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12)));
        presenterStyle.Setters.Add(new Setter(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled));
        presenterStyle.Setters.Add(new Setter(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled));
        flyout.FlyoutPresenterStyle = presenterStyle;

        // A definite height gives the star row a finite viewport even in a compact owner window.
        var root = new Grid { Width = 396, Height = 520 };
        for (int row = 0; row < 7; row++)
        {
            root.RowDefinitions.Add(new RowDefinition
            {
                Height = row == 3 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto
            });
        }

        void AddRow(FrameworkElement element, int row)
        {
            Grid.SetRow(element, row);
            root.Children.Add(element);
        }

        var searchBox = new AutoSuggestBox
        {
            PlaceholderText = UiText.Get("icons.searchPlaceholder"),
            QueryIcon = new SymbolIcon(Symbol.Find),
            Margin = new Thickness(0, 0, 0, 8)
        };
        AddRow(searchBox, 0);
        var tabBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(0, 0, 0, 8) };
        AddRow(tabBar, 1);
        var contentPanel = new StackPanel();
        var contentScroller = new ScrollViewer
        {
            Content = contentPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled,
            MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        // The large symbol catalogs own their scrolling viewport. The legacy scroller is
        // a sibling used only for the small preset/image lists, never their ancestor.
        var contentHost = new Grid { MinHeight = 0 };
        contentHost.Children.Add(contentScroller);
        AddRow(contentHost, 3);
        var attribution = CreateSelfhStAttributionBar();
        AddRow(attribution, 5);

        var confirmButton = new Button
        {
            Content = UiText.Get("icons.confirm"),
            Style = (Style)Application.Current.Resources["AccentButtonStyle"]
        };
        var cancelButton = new Button { Content = UiText.Get("common.cancel") };
        var confirmBar = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8, Margin = new Thickness(0, 8, 0, 0)
        };
        confirmBar.Children.Add(confirmButton);
        confirmBar.Children.Add(cancelButton);
        AddRow(confirmBar, 6);

        var bottomBar = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0)
        };
        if (onReset != null)
        {
            var resetButton = new HyperlinkButton { Content = UiText.Get("icons.resetToDefault"), Padding = new Thickness(0) };
            resetButton.Click += (_, _) =>
            {
                flyout.Hide();
                onReset();
            };
            bottomBar.Children.Add(resetButton);
        }
        AddRow(bottomBar, 4);

        int activeTab = TabAppIcons;
        int requestVersion = 0;
        bool changingTab = false;
        bool galleryActive = false;
        IconResult? pending = null;
        Button? selectedButton = null;
        StackPanel? palette = null;
        Func<string> getColor = () => "";
        var tabs = new List<(int Tab, Button Button)>();
        Action? cancelOptionReveal = null;
        bool revealRestoredOption = false;
        AutomaticPageIconChoice? pageIconChoice = null;

        void Highlight(Button button)
        {
            if (selectedButton != null)
            {
                selectedButton.BorderThickness = new Thickness(0);
                selectedButton.BorderBrush = null;
            }
            selectedButton = button;
            button.BorderThickness = new Thickness(2);
            button.BorderBrush = GetAccentBrush();
            if (revealRestoredOption)
            {
                revealRestoredOption = false;
                int version = requestVersion;
                cancelOptionReveal = SelectionReveal.AfterLayout(contentPanel,
                    () => galleryActive && version == requestVersion && ReferenceEquals(selectedButton, button),
                    () => button.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false }));
            }
        }

        void SetPending(IconResult selection)
        {
            pending = selection;
            confirmButton.IsEnabled = true;
        }

        var catalogView = new IconCatalogView(glyph =>
        {
            SetPending(new IconResult(glyph, null, Color: IsFluentGlyph(glyph) ? getColor() : null));
        });
        contentHost.Children.Add(catalogView);

        void RefreshContent(bool revealSelection = false)
        {
            cancelOptionReveal?.Invoke();
            cancelOptionReveal = null;
            revealRestoredOption = revealSelection;
            int version = ++requestVersion;
            selectedButton = null;
            if (pageIconChoice != null)
            {
                // This option survives content refreshes so its async preview is not restarted.
                pageIconChoice.SelectButton.BorderThickness = new Thickness(0);
                pageIconChoice.SelectButton.BorderBrush = null;
            }
            foreach (var (tab, button) in tabs)
            {
                button.Background = tab == activeTab ? GetAccentBrush() : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                button.Foreground = tab == activeTab
                    ? new SolidColorBrush(Microsoft.UI.Colors.White)
                    : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
            }
            palette!.Visibility = activeTab == TabGlyphs ? Visibility.Visible : Visibility.Collapsed;
            attribution.Visibility = activeTab == TabSelfhSt ? Visibility.Visible : Visibility.Collapsed;
            contentPanel.Children.Clear();
            catalogView.Clear();
            bool symbols = activeTab is TabGlyphs or TabEmoji;
            catalogView.Visibility = symbols ? Visibility.Visible : Visibility.Collapsed;
            contentScroller.Visibility = symbols ? Visibility.Collapsed : Visibility.Visible;
            string query = searchBox.Text.Trim();

            switch (activeTab)
            {
                case TabGlyphs:
                    catalogView.ShowCatalog(FluentIconCategories, query, emoji: false,
                        ParseHexBrush(getColor()) ?? (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"], pending?.Glyph, revealSelection);
                    break;
                case TabEmoji:
                    catalogView.ShowCatalog(EmojiCategories, query, emoji: true,
                        (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"], pending?.Glyph, revealSelection);
                    break;
                case TabAppIcons when launcherPresets:
                    BuildLauncherPresetsContent(contentPanel, query,
                        mode => SetPending(new IconResult(null, null, mode)), Highlight, pending?.PresetMode,
                        pageIconChoice, pending?.UsePageIcon == true);
                    BuildBrowseOption(contentPanel, query, onBrowseRequested, flyout, Highlight,
                        !string.IsNullOrEmpty(pending?.ImagePath) && selectedButton == null);
                    break;
                case TabAppIcons:
                    BuildAppIconsContent(contentPanel, query,
                        path => SetPending(new IconResult(null, path)), Highlight, pending?.ImagePath);
                    BuildBrowseOption(contentPanel, query, onBrowseRequested, flyout, Highlight,
                        !string.IsNullOrEmpty(pending?.ImagePath) && selectedButton == null);
                    break;
                case TabSelfhSt:
                    BuildSelfhStLoadingContent(contentPanel);
                    _ = LoadSelfhStContentAsync(contentPanel, query,
                        path => SetPending(new IconResult(null, path)), Highlight, pending?.ImagePath,
                        () => version == requestVersion && activeTab == TabSelfhSt && galleryActive);
                    break;
            }
        }

        void AddTab(int tab, UIElement icon, string label)
        {
            var button = MakeTabButton(icon, label);
            button.Click += (_, _) =>
            {
                changingTab = true;
                searchBox.Text = "";
                changingTab = false;
                activeTab = tab;
                contentScroller.ChangeView(0, 0, null);
                RefreshContent();
            };
            tabs.Add((tab, button));
            tabBar.Children.Add(button);
        }

        AddTab(TabAppIcons, new FontIcon { Glyph = "\uE737", FontSize = 16 },
            UiText.Get(launcherPresets ? "icons.tab.launcherPresets" : "icons.tab.appIcons"));
        AddTab(TabGlyphs, new FontIcon { Glyph = "\uE790", FontSize = 16 }, "Segoe Fluent Icons");
        AddTab(TabEmoji, new TextBlock { Text = "😀", FontFamily = new FontFamily("Segoe UI Emoji"), IsColorFontEnabled = true, FontSize = 16 }, UiText.Get("icons.tab.emoji"));
        AddTab(TabSelfhSt, new TextBlock { Text = "sh", FontSize = 13, FontWeight = FontWeights.Bold }, UiText.Get("icons.tab.selfhSt"));

        searchBox.TextChanged += (_, _) =>
        {
            if (!changingTab && palette != null)
            {
                contentScroller.ChangeView(0, 0, null);
                RefreshContent();
            }
        };
        confirmButton.Click += (_, _) =>
        {
            if (pending != null)
            {
                var result = pending;
                if (result.UsePageIcon)
                {
                    result = result with { PageIconPreview = pageIconChoice?.Preview };
                }
                if (result.Glyph != null)
                {
                    result = result with { Color = IsFluentGlyph(result.Glyph) ? getColor() : null };
                }
                flyout.Hide();
                onSelected(result);
            }
        };
        cancelButton.Click += (_, _) => flyout.Hide();
        flyout.Closed += (_, _) =>
        {
            galleryActive = false;
            pageIconChoice?.Dispose();
            pageIconChoice = null;
            requestVersion++;
            cancelOptionReveal?.Invoke();
            cancelOptionReveal = null;
            catalogView.Clear();
            contentPanel.Children.Clear();
        };
        flyout.Content = root;
        flyout.Opening += (_, _) =>
        {
            galleryActive = true;
            pageIconChoice?.Dispose();
            pageIconChoice = null;
            if (canUsePageIcon?.Invoke() == true && getPageIconPreview != null)
            {
                pageIconChoice = new AutomaticPageIconChoice(getPageIconPreview(),
                    () =>
                    {
                        Highlight(pageIconChoice!.SelectButton);
                        SetPending(new IconResult(null, null, UsePageIcon: true));
                    },
                    () =>
                    {
                        // A fetch finishing while another tile is selected never changes it.
                        if (galleryActive && pending?.UsePageIcon == true)
                        {
                            pending = pending with { PageIconPreview = pageIconChoice?.Preview };
                        }
                    });
            }
            SizeGalleryToWorkArea(root, ownerHwnd);
            var current = getCurrentSelection();
            // Item icons can retain a fallback glyph alongside an image. The image wins
            // in every preview and must also win when re-confirming that saved selection.
            bool hasImage = !string.IsNullOrEmpty(current.ImagePath);
            pending = current with
            {
                Glyph = hasImage || string.IsNullOrEmpty(current.Glyph) ? null : current.Glyph,
                ImagePath = hasImage ? current.ImagePath : null,
                PresetMode = hasImage || string.IsNullOrEmpty(current.PresetMode) ? null : current.PresetMode,
                Color = hasImage ? null : current.Color
            };
            confirmButton.IsEnabled = pending.Glyph != null || pending.ImagePath != null || pending.PresetMode != null || pending.UsePageIcon;
            changingTab = true;
            searchBox.Text = "";
            changingTab = false;
            if (palette != null)
            {
                root.Children.Remove(palette);
            }
            (palette, getColor) = BuildColorPalette(pending.Color, () => RefreshContent());
            AddRow(palette, 2);
            activeTab = GetSelectionTab(pending, launcherPresets);
            RefreshContent(revealSelection: true);
            if (pageIconChoice != null)
            {
                _ = pageIconChoice.LoadCachedAsync();
            }
        };
        flyout.Opened += (_, _) =>
        {
            SizeGalleryToWorkArea(root, ownerHwnd);
        };
        return flyout;
    }

    /// <summary>Finds the tab containing a saved selection, retaining custom image selections.</summary>
    private static int GetSelectionTab(IconResult selection, bool launcherPresets)
    {
        if (!string.IsNullOrEmpty(selection.ImagePath))
        {
            if (IsSelfhStImagePath(selection.ImagePath))
            {
                return TabSelfhSt;
            }
            return TabAppIcons;
        }
        if (!string.IsNullOrEmpty(selection.Glyph))
        {
            return IsFluentGlyph(selection.Glyph) ? TabGlyphs : TabEmoji;
        }
        return TabAppIcons;
    }

    /// <summary>
    /// Caps the complete gallery to the owning monitor's visible work area in DIPs.
    /// Owner-window height is deliberately irrelevant: new launchers have compact settings windows.
    /// </summary>
    private static void SizeGalleryToWorkArea(FrameworkElement root, IntPtr ownerHwnd)
    {
        double scale = ownerHwnd != IntPtr.Zero
            ? GetDpiForWindow(ownerHwnd) / 96.0
            : root.XamlRoot?.RasterizationScale ?? 1;
        if (scale <= 0)
        {
            scale = 1;
        }
        double height = root.XamlRoot?.Size.Height ?? 600;
        double width = root.XamlRoot?.Size.Width ?? 440;
        if (ownerHwnd != IntPtr.Zero)
        {
            var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                Microsoft.UI.Win32Interop.GetWindowIdFromWindow(ownerHwnd),
                Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
            if (area != null)
            {
                height = area.WorkArea.Height / scale;
                width = area.WorkArea.Width / scale;
            }
        }
        // Includes presenter padding plus breathing room against the taskbar/screen edges.
        root.Height = Math.Max(1, Math.Min(520, height - 48));
        root.Width = Math.Max(1, Math.Min(396, width - 48));
    }

    // ═══════════════════════════════════════════════════════════
    //  Tab Content Builders
    // ═══════════════════════════════════════════════════════════

    private static void BuildAppIconsContent(StackPanel panel, string query, Action<string> onImageSelected, Action<Button>? onButtonClicked = null, string? currentImagePath = null)
    {
        var filtered = FilterAppColorIcons(query);

        if (filtered.Length == 0)
        {
            panel.Children.Add(NoResults());
            return;
        }

        panel.Children.Add(CategoryHeader(UiText.Get("icons.appColorIcons")));

        var grid = CreateOptionWrapPanel(68, 76);

        foreach (var (color, nameKey) in filtered)
        {
            string sourcePath = Path.Combine(AppContext.BaseDirectory, "Resources", "AppIcons", $"{color}.png");
            if (!File.Exists(sourcePath)) continue;

            var btn = new Button
            {
                Width = 60,
                Height = 72,
                Padding = new Thickness(4),
                Margin = new Thickness(2),
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0)
            };

            var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            var img = new Image
            {
                Width = 36,
                Height = 36,
                Source = new BitmapImage(new Uri(sourcePath, UriKind.Absolute)),
                Margin = new Thickness(0, 0, 0, 4)
            };
            content.Children.Add(img);
            string colorName = UiText.Get(nameKey);
            content.Children.Add(new TextBlock
            {
                Text = colorName,
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            btn.Content = content;
            ToolTipService.SetToolTip(btn, UiText.Format("icons.appColorTooltip", ("color", colorName)));

            btn.Click += (s, e) =>
            {
                onButtonClicked?.Invoke((Button)s!);
                // Copy the app icon to the cache directory so IconPath can reference it
                string cacheDir = GetItemIconCacheDir();
                string dest = Path.Combine(cacheDir, $"app-{color.ToLowerInvariant()}.png");
                File.Copy(sourcePath, dest, true);
                onImageSelected(dest);
            };

            // Pre-select if this matches the current icon
            if (currentImagePath != null &&
                AreSameIconPath(currentImagePath, Path.Combine(ItemIconCacheDirectory, $"app-{color.ToLowerInvariant()}.png")))
            {
                onButtonClicked?.Invoke(btn);
                string cacheDir = GetItemIconCacheDir();
                string dest = Path.Combine(cacheDir, $"app-{color.ToLowerInvariant()}.png");
                File.Copy(sourcePath, dest, true);
                onImageSelected(dest);
            }

            grid.Children.Add(btn);
        }

        panel.Children.Add(grid);
    }

    private static void BuildSelfhStLoadingContent(StackPanel panel)
    {
        panel.Children.Clear();
        panel.Children.Add(new ProgressRing
        {
            IsActive = true,
            Width = 28,
            Height = 28,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 28, 0, 12)
        });
        panel.Children.Add(new TextBlock
        {
            Text = UiText.Get("icons.selfhStLoading"),
            FontSize = 13,
            Opacity = 0.7,
            HorizontalAlignment = HorizontalAlignment.Center
        });
    }

    private static async Task LoadSelfhStContentAsync(
        StackPanel panel,
        string query,
        Action<string> onImageSelected,
        Action<Button>? onButtonClicked = null,
        string? currentImagePath = null,
        Func<bool>? isCurrentRequest = null)
    {
        try
        {
            var catalog = await GetSelfhStCatalogAsync();
            if (isCurrentRequest?.Invoke() == false)
                return;

            var visible = FilterSelfhStIcons(catalog, query).Take(MaxVisibleSelfhStIcons).ToList();
            if (string.IsNullOrWhiteSpace(query) && !string.IsNullOrEmpty(currentImagePath))
            {
                // Keep thumbnail work bounded while reserving a place for a saved choice
                // outside the first page. That choice must still be highlighted and revealed.
                var current = catalog.FirstOrDefault(icon => IsMatchingSelfhStImagePath(currentImagePath, icon.Reference));
                if (current != null && !visible.Contains(current))
                {
                    if (visible.Count == MaxVisibleSelfhStIcons)
                    {
                        visible.RemoveAt(visible.Count - 1);
                    }
                    visible.Add(current);
                }
            }
            var filtered = visible.ToArray();

            panel.Children.Clear();
            panel.Children.Add(CategoryHeader(UiText.Get("icons.tab.selfhSt")));

            if (string.IsNullOrWhiteSpace(query) && catalog.Count > MaxVisibleSelfhStIcons)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = UiText.Plural("icons.selfhStFirstIcons", MaxVisibleSelfhStIcons),
                    FontSize = 12,
                    Opacity = 0.65,
                    Margin = new Thickness(4, 0, 0, 8),
                    TextWrapping = TextWrapping.WrapWholeWords
                });
            }

            if (filtered.Length == 0)
            {
                panel.Children.Add(NoResults());
                return;
            }

            var grid = CreateOptionWrapPanel(96, 96);
            int selectionRequestVersion = 0;

            foreach (var icon in filtered)
            {
                string reference = icon.Reference;

                var btn = new Button
                {
                    Width = 88,
                    Height = 92,
                    Padding = new Thickness(4),
                    Margin = new Thickness(2),
                    CornerRadius = new CornerRadius(6),
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    BorderThickness = new Thickness(0)
                };

                var content = new StackPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Spacing = 4
                };

                var thumbnail = new Image { Width = 32, Height = 32 };
                ShowSelfhStThumbnail(thumbnail, reference, isCurrentRequest);
                content.Children.Add(thumbnail);

                content.Children.Add(new TextBlock
                {
                    Text = icon.Name,
                    FontSize = 11,
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.WrapWholeWords,
                    MaxWidth = 76,
                    MaxLines = 2,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    HorizontalAlignment = HorizontalAlignment.Center
                });

                btn.Content = content;
                ToolTipService.SetToolTip(btn, icon.Name);
                btn.Click += async (s, e) =>
                {
                    // Downloads may finish out of click order. Only the latest choice may
                    // replace pending selection, even when the tab/query is still unchanged.
                    int selectionVersion = ++selectionRequestVersion;
                    string? localPath = await CacheSelfhStIconAsync(icon);
                    if (selectionVersion != selectionRequestVersion ||
                        string.IsNullOrEmpty(localPath) || isCurrentRequest?.Invoke() == false)
                    {
                        return;
                    }

                    onButtonClicked?.Invoke((Button)s!);
                    onImageSelected(localPath);
                };

                if (IsMatchingSelfhStImagePath(currentImagePath, reference))
                {
                    onButtonClicked?.Invoke(btn);
                    onImageSelected(currentImagePath!);
                }

                grid.Children.Add(btn);
            }

            panel.Children.Add(grid);
        }
        catch
        {
            if (isCurrentRequest?.Invoke() == false)
                return;

            panel.Children.Clear();
            panel.Children.Add(new TextBlock
            {
                Text = UiText.Get("icons.selfhStLoadFailed"),
                FontSize = 13,
                Opacity = 0.7,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.WrapWholeWords,
                Margin = new Thickness(16, 24, 16, 0)
            });
        }
    }

    private static void BuildLauncherPresetsContent(StackPanel panel, string query,
        Action<string> onPresetMode, Action<Button>? onButtonClicked = null, string? currentPresetMode = null,
        AutomaticPageIconChoice? pageIconChoice = null, bool usePageIconSelected = false)
    {
        if (pageIconChoice != null && MatchesQuery(query, "use page icon", UiText.Get("icons.pageIcon.choice")))
        {
            pageIconChoice.Margin = new Thickness(0, 0, 0, 8);
            if (usePageIconSelected)
            {
                onButtonClicked?.Invoke(pageIconChoice.SelectButton);
            }
            panel.Children.Add(pageIconChoice);
        }

        // ── Composite mode ──
        if (MatchesQuery(query, "composite", UiText.Get("icons.composite")))
        {
            panel.Children.Add(CategoryHeader(UiText.Get("icons.special")));
            var compositeBtn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 8),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0)
            };
            compositeBtn.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            compositeBtn.Content = CreateOptionContent(new FontIcon { Glyph = "\uF0E2", FontSize = 20 },
                UiText.Get("icons.composite"), UiText.Get("icons.compositeDescription"));
            compositeBtn.Click += (s, e) => { onButtonClicked?.Invoke((Button)s!); onPresetMode("Composite"); };
            if (currentPresetMode == "Composite") { onButtonClicked?.Invoke(compositeBtn); onPresetMode("Composite"); }
            panel.Children.Add(compositeBtn);
        }

        // ── Color preset icons (wrapping) ──
        var filteredColors = FilterAppColorIcons(query);

        if (filteredColors.Length > 0)
        {
            panel.Children.Add(CategoryHeader(UiText.Get("icons.appColorIcons")));

            var wrapGrid = CreateOptionWrapPanel(68, 76);

            foreach (var (color, nameKey) in filteredColors)
            {
                string sourcePath = Path.Combine(AppContext.BaseDirectory, "Resources", "AppIcons", $"{color}.png");
                if (!File.Exists(sourcePath)) continue;

                var btn = new Button
                {
                    Width = 60,
                    Height = 72,
                    Padding = new Thickness(4),
                    Margin = new Thickness(2),
                    CornerRadius = new CornerRadius(6),
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    BorderThickness = new Thickness(0)
                };

                var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                content.Children.Add(new Image
                {
                    Width = 36, Height = 36,
                    Source = new BitmapImage(new Uri(sourcePath, UriKind.Absolute)),
                    Margin = new Thickness(0, 0, 0, 4)
                });
                string colorName = UiText.Get(nameKey);
                content.Children.Add(new TextBlock
                {
                    Text = colorName, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center
                });
                btn.Content = content;
                ToolTipService.SetToolTip(btn, UiText.Format("icons.appColorTooltip", ("color", colorName)));
                btn.Click += (s, e) => { onButtonClicked?.Invoke((Button)s!); onPresetMode(color); };
                if (currentPresetMode == color) { onButtonClicked?.Invoke(btn); onPresetMode(color); }
                wrapGrid.Children.Add(btn);
            }

            panel.Children.Add(wrapGrid);
        }

        if (panel.Children.Count == 0)
            panel.Children.Add(NoResults());
    }

    /// <summary>Wraps only small image-option lists; large symbol catalogs keep native UI virtualization.</summary>
    private static VariableSizedWrapGrid CreateOptionWrapPanel(double width, double height) => new()
    {
        Orientation = Orientation.Horizontal,
        ItemWidth = width,
        ItemHeight = height,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };

    /// <summary>Constrains option labels to the available row width instead of expanding the scroll extent.</summary>
    private static Grid CreateOptionContent(FrameworkElement preview, string label, string? description = null)
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        preview.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(preview);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.Medium, TextWrapping = TextWrapping.Wrap });
        if (description != null)
        {
            text.Children.Add(new TextBlock { Text = description, FontSize = 12, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });
        }
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        return row;
    }

    /// <summary>Offers custom image browsing only inside the first options tab.</summary>
    private static void BuildBrowseOption(StackPanel panel, string query, Action browse, Flyout flyout,
        Action<Button> highlight, bool currentImageIsSelected)
    {
        if (!MatchesQuery(query, "browse image custom", UiText.Get("icons.browseImage")))
        {
            return;
        }
        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(12, 8, 12, 8),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Content = CreateOptionContent(new FontIcon { Glyph = "\uE8B9", FontSize = 20 }, UiText.Get("icons.browseImage"))
        };
        AutomationProperties.SetName(button, UiText.Get("icons.browseImageAccessibleName"));
        button.Click += (_, _) =>
        {
            flyout.Hide();
            browse();
        };
        if (currentImageIsSelected)
        {
            highlight(button);
        }
        panel.Children.Add(button);
    }

    // ═══════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// App color icon presets: the English id names the bundled PNG and the saved preset mode,
    /// so it never changes with the interface language; only the shown name is translated.
    /// </summary>
    private static readonly (string Id, string NameKey)[] AppColorIcons =
    [
        ("Blue", "icons.color.blue"),
        ("Green", "icons.color.green"),
        ("Teal", "icons.color.teal"),
        ("Red", "icons.color.red"),
        ("Orange", "icons.color.orange"),
        ("Purple", "icons.color.purple"),
    ];

    /// <summary>App color icons whose English id or translated name contains the query.</summary>
    private static (string Id, string NameKey)[] FilterAppColorIcons(string query) =>
        AppColorIcons.Where(color => MatchesQuery(query, color.Id, UiText.Get(color.NameKey))).ToArray();

    /// <summary>
    /// True when the query is empty or occurs in the English search terms or the translated
    /// label, so English searches keep working whatever the interface language is.
    /// </summary>
    private static bool MatchesQuery(string query, string englishTerms, string translatedLabel) =>
        string.IsNullOrEmpty(query)
        || englishTerms.Contains(query, StringComparison.OrdinalIgnoreCase)
        || translatedLabel.Contains(query, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns true if the glyph is a Segoe Fluent Icons private-use-area character.
    /// Returns false for emojis and standard Unicode characters.
    /// </summary>
    public static bool IsFluentGlyph(string? glyph)
    {
        if (string.IsNullOrEmpty(glyph)) return true; // treat default/empty as fluent
        int cp = char.IsHighSurrogate(glyph[0]) && glyph.Length > 1
            ? char.ConvertToUtf32(glyph[0], glyph[1])
            : glyph[0];
        return cp >= 0xE000 && cp <= 0xF8FF;
    }

    private static Button MakeTabButton(UIElement icon, string tooltip)
    {
        var btn = new Button
        {
            Content = icon,
            Width = 40,
            Height = 36,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0)
        };
        ToolTipService.SetToolTip(btn, tooltip);
        AutomationProperties.SetName(btn, tooltip);
        return btn;
    }

    private static TextBlock CategoryHeader(string text) => new()
    {
        Text = text,
        FontSize = 12,
        FontWeight = FontWeights.SemiBold,
        Opacity = 0.7,
        Margin = new Thickness(4, 4, 0, 6)
    };

    internal static TextBlock NoResults() => new()
    {
        Text = UiText.Get("icons.noMatches"),
        FontSize = 13,
        Opacity = 0.5,
        HorizontalAlignment = HorizontalAlignment.Center,
        Margin = new Thickness(0, 24, 0, 0)
    };

    /// <summary>
    /// Builds the selfh.st credit line from one translated sentence. The sentence is split at its
    /// <c>{source}</c> and <c>{license}</c> placeholders so each language can order the two
    /// links as its grammar needs; the link labels are names and stay untranslated.
    /// </summary>
    private static FrameworkElement CreateSelfhStAttributionBar()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 0,
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed
        };

        string template = UiText.Get("icons.selfhStAttribution");

        // The capture group keeps each placeholder in the split result, in sentence order.
        foreach (string part in System.Text.RegularExpressions.Regex.Split(template, @"(\{source\}|\{license\})"))
        {
            if (part.Length == 0)
            {
                continue;
            }

            if (part == "{source}")
            {
                row.Children.Add(CreateAttributionLink("selfh.st", SelfhStIconsUri));
            }
            else if (part == "{license}")
            {
                row.Children.Add(CreateAttributionLink("CC BY 4.0", SelfhStLicenseUri));
            }
            else
            {
                // Spaces around the links come from the sentence itself.
                row.Children.Add(new TextBlock
                {
                    Text = part,
                    FontSize = 12,
                    Opacity = 0.7,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
        }

        return row;
    }

    /// <summary>A borderless inline link for the attribution line.</summary>
    private static HyperlinkButton CreateAttributionLink(string label, Uri target) => new()
    {
        Content = label,
        NavigateUri = target,
        Padding = new Thickness(0),
        FontSize = 12,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static string ItemIconCacheDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LittleLauncher", "icons");

    private static string GetItemIconCacheDir()
    {
        string cacheDir = ItemIconCacheDirectory;
        Directory.CreateDirectory(cacheDir);
        return cacheDir;
    }

    private static string GetSelfhStPngUrl(string reference) =>
        $"{SelfhStPngUrlPrefix}{Uri.EscapeDataString(reference)}.png";

    /// <summary>
    /// Where browsed selfh.st icons are kept, separate from the icons folder itself.
    /// </summary>
    /// <remarks>
    /// Browsing the tab caches every icon it shows, which is a great many more than the user will
    /// ever pick. Keeping them out of <c>icons</c> leaves that folder as what it has always been —
    /// the icons actually in use — so nothing else has to learn to tell the two apart.
    /// </remarks>
    private static string GetSelfhStCacheDir()
    {
        string cacheDir = Path.Combine(GetItemIconCacheDir(), "selfhst");
        Directory.CreateDirectory(cacheDir);
        return cacheDir;
    }

    /// <summary>Reference names come from a remote index, so they are never trusted as a filename.</summary>
    private static string SelfhStFileName(string reference)
    {
        var name = new System.Text.StringBuilder(reference.Length);
        foreach (char c in reference.ToLowerInvariant())
            name.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
        return name.ToString();
    }

    private static string GetSelfhStCachePath(string reference) =>
        Path.Combine(GetSelfhStCacheDir(), $"{SelfhStFileName(reference)}.png");

    private static string GetSelfhStCatalogPath() =>
        Path.Combine(GetSelfhStCacheDir(), "index.json");

    /// <summary>
    /// Points a gallery tile at its icon: straight from disk when it has been seen before,
    /// otherwise downloaded once and kept.
    /// </summary>
    /// <remarks>
    /// Binding the tile to the CDN URL directly was what made this tab slow every single time it
    /// was opened — nothing survived the process, so a hundred icons were re-fetched over the wire
    /// on each visit. Downloading them here instead costs the same first time and nothing after.
    /// </remarks>
    private static void ShowSelfhStThumbnail(Image image, string reference, Func<bool>? isCurrentRequest)
    {
        string cachePath = GetSelfhStCachePath(reference);
        if (File.Exists(cachePath))
        {
            image.Source = CreateSelfhStBitmap(cachePath);
            return;
        }

        _ = LoadSelfhStThumbnailAsync(image, reference, cachePath, isCurrentRequest);
    }

    private static async Task LoadSelfhStThumbnailAsync(
        Image image, string reference, string cachePath, Func<bool>? isCurrentRequest)
    {
        var dispatcher = image.DispatcherQueue;

        await SelfhStDownloadGate.WaitAsync();
        try
        {
            // The user has typed on since this tile was built; the panel it lives in is gone.
            if (isCurrentRequest?.Invoke() == false)
                return;

            byte[] bytes = await SelfhStHttp.GetByteArrayAsync(GetSelfhStPngUrl(reference));
            await WriteSelfhStCacheFileAsync(cachePath, bytes);
        }
        catch
        {
            return;   // a tile without an icon, rather than an error over the whole tab
        }
        finally
        {
            SelfhStDownloadGate.Release();
        }

        dispatcher?.TryEnqueue(() => image.Source = CreateSelfhStBitmap(cachePath));
    }

    private static BitmapImage CreateSelfhStBitmap(string path) => new()
    {
        DecodePixelWidth = 32,
        UriSource = new Uri(path),
    };

    /// <summary>
    /// Writes a cache file whole or not at all.
    /// </summary>
    /// <remarks>
    /// A cached file is never re-downloaded, so a half-written one is permanent: an interrupted
    /// write would leave a tile that renders blank for good. Writing beside it and moving into
    /// place also settles the race between two galleries caching the same icon at once.
    /// </remarks>
    private static async Task WriteSelfhStCacheFileAsync(string path, byte[] bytes)
    {
        string tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(tempPath, bytes);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { /* nothing further to do */ }
            throw;
        }
    }

    /// <summary>Recognizes selfh.st selections only within the gallery's persisted item cache.</summary>
    private static bool IsSelfhStImagePath(string? path) =>
        !string.IsNullOrEmpty(path) &&
        AreSameIconPath(Path.GetDirectoryName(path), ItemIconCacheDirectory) &&
        Path.GetFileName(path).StartsWith("selfhst-", StringComparison.OrdinalIgnoreCase);

    private static bool IsMatchingSelfhStImagePath(string? path, string reference) =>
        AreSameIconPath(path, Path.Combine(ItemIconCacheDirectory, $"selfhst-{SelfhStFileName(reference)}.png"));

    /// <summary>Compares normalized Windows paths without confusing user filenames with cache provenance.</summary>
    private static bool AreSameIconPath(string? left, string? right)
    {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
        {
            return false;
        }
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static IEnumerable<SelfhStIconEntry> FilterSelfhStIcons(IEnumerable<SelfhStIconEntry> icons, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return icons;

        return icons.Where(icon =>
            icon.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            icon.Reference.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrEmpty(icon.Tags) && icon.Tags.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(icon.Category) && icon.Category.Contains(query, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// The icon index, from memory, then disk, then the network.
    /// </summary>
    /// <remarks>
    /// The disk copy is what makes the tab open instantly rather than on a spinner: the in-memory
    /// copy dies with the process, and the index is a megabyte of JSON fetched before a single icon
    /// can be drawn. A stale copy is also better than none — if the fetch fails, whatever was last
    /// written is served rather than failing the whole tab, and it is treated as current for the
    /// rest of the session so every keystroke does not re-attempt a download that just timed out.
    /// </remarks>
    private static async Task<IReadOnlyList<SelfhStIconEntry>> GetSelfhStCatalogAsync()
    {
        if (_selfhStCatalog != null && DateTimeOffset.UtcNow - _selfhStCatalogFetchedAt < SelfhStCatalogTtl)
            return _selfhStCatalog;

        await SelfhStCatalogLock.WaitAsync();
        try
        {
            if (_selfhStCatalog != null && DateTimeOffset.UtcNow - _selfhStCatalogFetchedAt < SelfhStCatalogTtl)
                return _selfhStCatalog;

            string catalogPath = GetSelfhStCatalogPath();
            var cached = ReadSelfhStCatalogFile(catalogPath);
            if (cached != null &&
                DateTime.UtcNow - File.GetLastWriteTimeUtc(catalogPath) < SelfhStCatalogTtl)
            {
                return CacheSelfhStCatalog(cached);
            }

            try
            {
                string json = await SelfhStHttp.GetStringAsync(SelfhStIndexUrl);
                var icons = ParseSelfhStCatalog(json);

                try
                {
                    await WriteSelfhStCacheFileAsync(catalogPath, System.Text.Encoding.UTF8.GetBytes(json));
                }
                catch { /* the index still works this session; it just re-fetches next time */ }

                return CacheSelfhStCatalog(icons);
            }
            catch when (cached != null)
            {
                return CacheSelfhStCatalog(cached);
            }
        }
        finally
        {
            SelfhStCatalogLock.Release();
        }
    }

    private static IReadOnlyList<SelfhStIconEntry> CacheSelfhStCatalog(IReadOnlyList<SelfhStIconEntry> icons)
    {
        _selfhStCatalog = icons;
        _selfhStCatalogFetchedAt = DateTimeOffset.UtcNow;
        return icons;
    }

    private static IReadOnlyList<SelfhStIconEntry> ParseSelfhStCatalog(string json) =>
        JsonSerializer.Deserialize<List<SelfhStIconEntry>>(json, SelfhStJsonOptions)?
            .Where(icon => !string.IsNullOrWhiteSpace(icon.Name) && !string.IsNullOrWhiteSpace(icon.Reference))
            .OrderBy(icon => icon.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray() ?? [];

    private static IReadOnlyList<SelfhStIconEntry>? ReadSelfhStCatalogFile(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            var icons = ParseSelfhStCatalog(File.ReadAllText(path));
            return icons.Count > 0 ? icons : null;
        }
        catch
        {
            return null;   // truncated or half-written: treat it as absent and re-fetch
        }
    }

    /// <summary>
    /// Puts the chosen icon at its permanent path in the icons folder, taking it from the
    /// browsing cache when it is already there rather than fetching it a second time.
    /// </summary>
    private static async Task<string?> CacheSelfhStIconAsync(SelfhStIconEntry icon)
    {
        string cachePath = Path.Combine(GetItemIconCacheDir(), $"selfhst-{SelfhStFileName(icon.Reference)}.png");
        if (File.Exists(cachePath))
            return cachePath;

        try
        {
            string browsePath = GetSelfhStCachePath(icon.Reference);
            if (File.Exists(browsePath))
            {
                File.Copy(browsePath, cachePath, overwrite: true);
                return cachePath;
            }

            byte[] bytes = await SelfhStHttp.GetByteArrayAsync(GetSelfhStPngUrl(icon.Reference));
            await File.WriteAllBytesAsync(cachePath, bytes);
            return cachePath;
        }
        catch
        {
            return null;
        }
    }

    private sealed class SelfhStIconEntry
    {
        [JsonPropertyName("Name")]
        public string Name { get; init; } = "";

        [JsonPropertyName("Reference")]
        public string Reference { get; init; } = "";

        [JsonPropertyName("Tags")]
        public string? Tags { get; init; }

        [JsonPropertyName("Category")]
        public string? Category { get; init; }
    }

    /// <summary>Preset glyph colors available in the icon gallery.</summary>
    private static readonly (string Hex, string NameKey)[] GlyphColors =
    [
        ("", "common.default"),
        ("#E74856", "icons.color.red"),
        ("#FF8C00", "icons.color.orange"),
        ("#FFC83D", "icons.color.yellow"),
        ("#47B353", "icons.color.green"),
        ("#00B7C3", "icons.color.teal"),
        ("#0078D4", "icons.color.blue"),
        ("#8764B8", "icons.color.purple"),
        ("#E3008C", "icons.color.pink"),
        ("#6B6B6B", "icons.color.gray"),
        ("#FFFFFF", "icons.color.white"),
        ("#000000", "icons.color.black"),
    ];

    private static SolidColorBrush? ParseHexBrush(string? hex)
    {
        if (string.IsNullOrEmpty(hex) || hex[0] != '#') return null;
        try
        {
            byte r = Convert.ToByte(hex.Substring(1, 2), 16);
            byte g = Convert.ToByte(hex.Substring(3, 2), 16);
            byte b = Convert.ToByte(hex.Substring(5, 2), 16);
            return new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, r, g, b));
        }
        catch { return null; }
    }

    /// <summary>
    /// Builds a color palette row. Returns the panel and a function to get the current selected color hex.
    /// </summary>
    private static (StackPanel Panel, Func<string> GetSelectedColor) BuildColorPalette(
        string? initialColor, Action? onColorChanged = null)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        string selectedColor = initialColor ?? "";
        Button? selectedButton = null;

        foreach (var (hex, nameKey) in GlyphColors)
        {
            var btn = new Button
            {
                Width = 28,
                Height = 28,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(14),
                BorderThickness = new Thickness(2),
                MinWidth = 0,
                MinHeight = 0,
            };

            Brush colorBrush;
            if (string.IsNullOrEmpty(hex))
            {
                colorBrush = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
            }
            else
            {
                byte r = Convert.ToByte(hex[1..3], 16);
                byte g = Convert.ToByte(hex[3..5], 16);
                byte b = Convert.ToByte(hex[5..7], 16);
                colorBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, r, g, b));
            }
            btn.Background = colorBrush;

            // Override hover/press backgrounds to keep the swatch color visible
            btn.Resources["ButtonBackgroundPointerOver"] = colorBrush;
            btn.Resources["ButtonBackgroundPressed"] = colorBrush;
            btn.Resources["ButtonBorderBrushPointerOver"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            btn.Resources["ButtonBorderBrushPressed"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

            void UpdateSelection(Button b, string h)
            {
                if (selectedButton != null)
                    selectedButton.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

                selectedButton = b;
                selectedColor = h;
                b.BorderBrush = GetAccentBrush();
                onColorChanged?.Invoke();
            }

            // Set initial selection
            if (hex.Equals(selectedColor, StringComparison.OrdinalIgnoreCase) ||
                (string.IsNullOrEmpty(hex) && string.IsNullOrEmpty(selectedColor)))
            {
                btn.BorderBrush = GetAccentBrush();
                selectedButton = btn;
            }
            else
            {
                btn.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            }

            ToolTipService.SetToolTip(btn, UiText.Get(nameKey));
            string capturedHex = hex;
            btn.Click += (s, e) => UpdateSelection((Button)s!, capturedHex);
            row.Children.Add(btn);
        }

        panel.Children.Add(new TextBlock
        {
            Text = UiText.Get("icons.colorLabel"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Opacity = 0.7,
            Margin = new Thickness(4, 0, 0, 4)
        });
        panel.Children.Add(row);
        return (panel, () => selectedColor);
    }

    private static Brush GetAccentBrush()
    {
        if (Application.Current.Resources.TryGetValue("AccentFillColorDefaultBrush", out var b))
            return (Brush)b;
        return new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue);
    }

    // ═══════════════════════════════════════════════════════════
    //  Icon Data — Segoe Fluent Icons
    // ═══════════════════════════════════════════════════════════

    private static readonly (string CategoryKey, (string Glyph, string NameKey)[] Icons)[] FluentIconCategories =
    [
        ("icons.category.webAndCommunication", [
            ("\uE774", "iconNames.globe"),
            ("\uF49A", "iconNames.globe2"),
            ("\uEB41", "iconNames.website"),
            ("\uE715", "iconNames.mail"),
            ("\uE8A8", "iconNames.mailFill"),
            ("\uE89C", "iconNames.mailForward"),
            ("\uE8CA", "iconNames.mailReply"),
            ("\uE8C2", "iconNames.mailReplyAll"),
            ("\uE717", "iconNames.phone"),
            ("\uE8EA", "iconNames.cellPhone"),
            ("\uE77E", "iconNames.incomingCall"),
            ("\uE778", "iconNames.hangUp"),
            ("\uE8BD", "iconNames.message"),
            ("\uE90A", "iconNames.comment"),
            ("\uEA91", "iconNames.thoughtBubble"),
            ("\uE8F2", "iconNames.chatBubbles"),
            ("\uEAB7", "iconNames.chatSparkle"),
            ("\uE8AA", "iconNames.videoChat"),
            ("\uE71B", "iconNames.link"),
            ("\uE72D", "iconNames.share"),
            ("\uE8EB", "iconNames.reshare"),
            ("\uE724", "iconNames.send"),
            ("\uE725", "iconNames.sendFill"),
            ("\uF3E2", "iconNames.nearbySharing"),
            ("\uE716", "iconNames.people"),
            ("\uE77B", "iconNames.contact"),
            ("\uE8D4", "iconNames.contact2"),
            ("\uE779", "iconNames.contactInfo"),
            ("\uE902", "iconNames.group"),
            ("\uEBDA", "iconNames.family"),
            ("\uE8FA", "iconNames.addFriend"),
            ("\uE8F8", "iconNames.blockContact"),
            ("\uE7EE", "iconNames.otherUser"),
            ("\uEE57", "iconNames.guestUser"),
            ("\uE910", "iconNames.accounts"),
            ("\uF3B1", "iconNames.signOut"),
            ("\uE701", "iconNames.wifi"),
            ("\uE702", "iconNames.bluetooth"),
            ("\uE839", "iconNames.ethernet"),
            ("\uE753", "iconNames.cloud"),
            ("\uEBD3", "iconNames.cloudDownload"),
            ("\uEDE4", "iconNames.cloudSearch"),
            ("\uE896", "iconNames.download"),
            ("\uE898", "iconNames.upload"),
            ("\uE703", "iconNames.connect"),
            ("\uE704", "iconNames.internetSharing"),
            ("\uEC05", "iconNames.networkTower"),
            ("\uE968", "iconNames.network"),
            ("\uF385", "iconNames.networkConnected"),
            ("\uF384", "iconNames.networkOffline"),
            ("\uF193", "iconNames.networkSharing"),
            ("\uEB77", "iconNames.gatewayRouter"),
            ("\uE705", "iconNames.vpn"),
            ("\uE88A", "iconNames.wifiHotspot"),
        ]),
        ("icons.category.filesAndDocuments", [
            ("\uE8E5", "iconNames.openFile"),
            ("\uE8A5", "iconNames.document"),
            ("\uE8A6", "iconNames.protectedDocument"),
            ("\uEA90", "iconNames.pdf"),
            ("\uE9F9", "iconNames.reportDocument"),
            ("\uF28B", "iconNames.documentApproval"),
            ("\uE8B7", "iconNames.folder"),
            ("\uE8D5", "iconNames.folderFill"),
            ("\uE838", "iconNames.folderOpen"),
            ("\uE8F4", "iconNames.newFolder"),
            ("\uED25", "iconNames.openFolder"),
            ("\uEC25", "iconNames.personalFolder"),
            ("\uF012", "iconNames.zipFolder"),
            ("\uE8DE", "iconNames.moveToFolder"),
            ("\uE74E", "iconNames.save"),
            ("\uE792", "iconNames.saveAs"),
            ("\uE78C", "iconNames.saveLocal"),
            ("\uEA35", "iconNames.saveCopy"),
            ("\uE8F1", "iconNames.library"),
            ("\uE7C3", "iconNames.page"),
            ("\uE8C8", "iconNames.copy"),
            ("\uF413", "iconNames.copyTo"),
            ("\uE77F", "iconNames.paste"),
            ("\uE8C6", "iconNames.cut"),
            ("\uE74D", "iconNames.delete"),
            ("\uE723", "iconNames.attach"),
            ("\uE710", "iconNames.add"),
            ("\uE711", "iconNames.cancel"),
            ("\uE738", "iconNames.remove"),
            ("\uF56D", "iconNames.printDefault"),
            ("\uE8A4", "iconNames.bookmarks"),
            ("\uE728", "iconNames.favoriteList"),
            ("\uE8C3", "iconNames.read"),
            ("\uE736", "iconNames.readingMode"),
            ("\uE7BC", "iconNames.readingList"),
            ("\uE8AC", "iconNames.rename"),
            ("\uE8EC", "iconNames.tag"),
            ("\uE932", "iconNames.label"),
            ("\uE762", "iconNames.multiSelect"),
            ("\uE8B3", "iconNames.selectAll"),
            ("\uE8CB", "iconNames.sort"),
            ("\uE71C", "iconNames.filter"),
            ("\uE8B5", "iconNames.import"),
            ("\uEDE1", "iconNames.export"),
            ("\uEC50", "iconNames.fileExplorer"),
            ("\uED41", "iconNames.treeFolder"),
            ("\uE8F7", "iconNames.syncFolder"),
            ("\uE8F6", "iconNames.unsyncFolder"),
            ("\uE8FF", "iconNames.preview"),
            ("\uE8A1", "iconNames.previewLink"),
        ]),
        ("icons.category.mediaAndEntertainment", [
            ("\uE768", "iconNames.play"),
            ("\uF5B0", "iconNames.playSolid"),
            ("\uE769", "iconNames.pause"),
            ("\uE71A", "iconNames.stop"),
            ("\uE8D6", "iconNames.audio"),
            ("\uEC4F", "iconNames.musicNote"),
            ("\uE93C", "iconNames.musicAlbum"),
            ("\uE90B", "iconNames.musicInfo"),
            ("\uE93E", "iconNames.streaming"),
            ("\uE722", "iconNames.camera"),
            ("\uE89E", "iconNames.rotateCamera"),
            ("\uE714", "iconNames.video"),
            ("\uF131", "iconNames.video360"),
            ("\uF7EE", "iconNames.videoCapture"),
            ("\uE91B", "iconNames.photo"),
            ("\uEB9F", "iconNames.photo2"),
            ("\uE7AA", "iconNames.photoCollection"),
            ("\uE8B9", "iconNames.picture"),
            ("\uE767", "iconNames.volume"),
            ("\uE74F", "iconNames.mute"),
            ("\uF4C3", "iconNames.mixVolumes"),
            ("\uE786", "iconNames.slideshow"),
            ("\uE720", "iconNames.microphone"),
            ("\uEC71", "iconNames.micOn"),
            ("\uEC54", "iconNames.micOff"),
            ("\uE7F6", "iconNames.headphone"),
            ("\uE95B", "iconNames.headset"),
            ("\uF4C0", "iconNames.earbud"),
            ("\uE7F5", "iconNames.speakers"),
            ("\uE7FC", "iconNames.game"),
            ("\uE967", "iconNames.gameConsole"),
            ("\uE899", "iconNames.emoji"),
            ("\uE76E", "iconNames.emoji2"),
            ("\uEB9D", "iconNames.fastForward"),
            ("\uEB9E", "iconNames.rewind"),
            ("\uE892", "iconNames.previous"),
            ("\uE893", "iconNames.next"),
            ("\uE8B1", "iconNames.shuffle"),
            ("\uE8EE", "iconNames.repeatAll"),
            ("\uE8ED", "iconNames.repeatOne"),
            ("\uEF3B", "iconNames.replay"),
            ("\uE8B2", "iconNames.movies"),
            ("\uE7AD", "iconNames.rotate"),
            ("\uE8AB", "iconNames.switch"),
            ("\uE8BA", "iconNames.caption"),
            ("\uED1E", "iconNames.subtitles"),
            ("\uE7F0", "iconNames.closedCaptions"),
            ("\uE7E6", "iconNames.highlight"),
            ("\uF4A9", "iconNames.gif"),
            ("\uE9E9", "iconNames.equalizer"),
        ]),
        ("icons.category.toolsAndDevelopment", [
            ("\uE756", "iconNames.commandPrompt"),
            ("\uE943", "iconNames.code"),
            ("\uEC7A", "iconNames.developerTools"),
            ("\uEBE8", "iconNames.bug"),
            ("\uE9D9", "iconNames.diagnostic"),
            ("\uE713", "iconNames.settings"),
            ("\uF8B0", "iconNames.settingsSolid"),
            ("\uE90F", "iconNames.repair"),
            ("\uE912", "iconNames.manage"),
            ("\uE721", "iconNames.search"),
            ("\uED37", "iconNames.searchSparkle"),
            ("\uF6FA", "iconNames.webSearch"),
            ("\uE70F", "iconNames.edit"),
            ("\uE72C", "iconNames.refresh"),
            ("\uE895", "iconNames.sync"),
            ("\uEA6A", "iconNames.syncError"),
            ("\uE790", "iconNames.color"),
            ("\uF354", "iconNames.colorSolid"),
            ("\uEF3C", "iconNames.eyedropper"),
            ("\uE8A3", "iconNames.zoomIn"),
            ("\uE71F", "iconNames.zoomOut"),
            ("\uE7A7", "iconNames.undo"),
            ("\uE7A6", "iconNames.redo"),
            ("\uE7A8", "iconNames.crop"),
            ("\uE7EF", "iconNames.admin"),
            ("\uED5E", "iconNames.ruler"),
            ("\uF0B4", "iconNames.protractor"),
            ("\uE7AC", "iconNames.openWith"),
            ("\uE8FE", "iconNames.scan"),
            ("\uEE6F", "iconNames.genericScan"),
            ("\uED14", "iconNames.qrCode"),
            ("\uE78B", "iconNames.newWindow"),
            ("\uE8A7", "iconNames.openInNewWindow"),
            ("\uE944", "iconNames.returnToWindow"),
            ("\uE90C", "iconNames.dockLeft"),
            ("\uE90D", "iconNames.dockRight"),
            ("\uE90E", "iconNames.dockBottom"),
            ("\uE952", "iconNames.dock"),
            ("\uF120", "iconNames.taskManager"),
            ("\uE950", "iconNames.component"),
            ("\uE9F3", "iconNames.process"),
            ("\uE82F", "iconNames.tooltip"),
            ("\uE76D", "iconNames.inkingTool"),
            ("\uED63", "iconNames.pencil"),
            ("\uED64", "iconNames.marker"),
            ("\uEC87", "iconNames.draw"),
            ("\uF196", "iconNames.beaker"),
            ("\uEB3C", "iconNames.design"),
            ("\uF406", "iconNames.clippingTool"),
            ("\uF7ED", "iconNames.windowSnipping"),
            ("\uEBC6", "iconNames.project"),
            ("\uE794", "iconNames.effects"),
            ("\uE799", "iconNames.aspectRatio"),
            ("\uEDA7", "iconNames.keyboardShortcut"),
            ("\uEA99", "iconNames.broom"),
            ("\uF034", "iconNames.widget"),
        ]),
        ("icons.category.textAndFormatting", [
            ("\uE8D2", "iconNames.font"),
            ("\uE8D3", "iconNames.fontColor"),
            ("\uE8E8", "iconNames.fontIncrease"),
            ("\uE8E7", "iconNames.fontDecrease"),
            ("\uE8E9", "iconNames.fontSize"),
            ("\uE8DD", "iconNames.bold"),
            ("\uE8DB", "iconNames.italic"),
            ("\uE8DC", "iconNames.underline"),
            ("\uEDE0", "iconNames.strikethrough"),
            ("\uE8E4", "iconNames.alignLeft"),
            ("\uE8E3", "iconNames.alignCenter"),
            ("\uE8E2", "iconNames.alignRight"),
            ("\uE8FD", "iconNames.bulletedList"),
            ("\uEA37", "iconNames.list"),
            ("\uE9D5", "iconNames.checklist"),
            ("\uE8C1", "iconNames.characters"),
            ("\uEC34", "iconNames.formatText"),
            ("\uEF60", "iconNames.textEdit"),
            ("\uE929", "iconNames.handwriting"),
            ("\uE82D", "iconNames.dictionary"),
            ("\uE87B", "iconNames.spelling"),
        ]),
        ("icons.category.shoppingAndFinance", [
            ("\uE719", "iconNames.shop"),
            ("\uE7BF", "iconNames.shoppingCart"),
            ("\uEAFC", "iconNames.market"),
            ("\uE8EF", "iconNames.calculator"),
            ("\uE825", "iconNames.bank"),
            ("\uEB95", "iconNames.certificate"),
            ("\uE8C7", "iconNames.paymentCard"),
            ("\uE7B8", "iconNames.package"),
            ("\uEB0F", "iconNames.stockUp"),
            ("\uEB11", "iconNames.stockDown"),
            ("\uE8C9", "iconNames.important"),
            ("\uE8D0", "iconNames.priority"),
            ("\uEC09", "iconNames.groceries"),
            ("\uE821", "iconNames.work"),
        ]),
        ("icons.category.calendarAndTime", [
            ("\uE787", "iconNames.calendar"),
            ("\uE8BF", "iconNames.calendarDay"),
            ("\uE8C0", "iconNames.calendarWeek"),
            ("\uE8D1", "iconNames.goToToday"),
            ("\uE8F5", "iconNames.calendarReply"),
            ("\uE917", "iconNames.clock"),
            ("\uE916", "iconNames.stopwatch"),
            ("\uEC92", "iconNames.dateTime"),
            ("\uE81C", "iconNames.history"),
            ("\uE823", "iconNames.recent"),
            ("\uE708", "iconNames.quietHours"),
            ("\uEB50", "iconNames.reminder"),
            ("\uF4BD", "iconNames.snooze"),
        ]),
        ("icons.category.placesAndTravel", [
            ("\uE80F", "iconNames.home"),
            ("\uEA8A", "iconNames.homeSolid"),
            ("\uEC06", "iconNames.city"),
            ("\uE7BE", "iconNames.education"),
            ("\uF7BB", "iconNames.educationIcon"),
            ("\uE822", "iconNames.construction"),
            ("\uE81D", "iconNames.location"),
            ("\uE707", "iconNames.mapPin"),
            ("\uE7B7", "iconNames.mapPin2"),
            ("\uE81E", "iconNames.mapLayers"),
            ("\uE8F0", "iconNames.directions"),
            ("\uE816", "iconNames.mapDirections"),
            ("\uE804", "iconNames.car"),
            ("\uE806", "iconNames.bus"),
            ("\uE7E3", "iconNames.ferry"),
            ("\uE7C0", "iconNames.train"),
            ("\uE709", "iconNames.airplane"),
            ("\uE909", "iconNames.world"),
            ("\uE805", "iconNames.walk"),
            ("\uE811", "iconNames.parkingLocation"),
            ("\uEC08", "iconNames.courthouse"),
            ("\uEC32", "iconNames.cafe"),
            ("\uEF31", "iconNames.trafficLight"),
            ("\uE81F", "iconNames.accident"),
            ("\uE98F", "iconNames.constructionCone"),
            ("\uE7EC", "iconNames.drivingMode"),
        ]),
        ("icons.category.devicesAndHardware", [
            ("\uEC4E", "iconNames.thisPC"),
            ("\uE977", "iconNames.pc"),
            ("\uE7F7", "iconNames.laptop"),
            ("\uF552", "iconNames.laptopSecure"),
            ("\uE7F4", "iconNames.tvMonitor"),
            ("\uE70A", "iconNames.tablet"),
            ("\uE8CC", "iconNames.mobileTablet"),
            ("\uE8B8", "iconNames.webcam"),
            ("\uE960", "iconNames.webcam2"),
            ("\uE914", "iconNames.threeDPrinter"),
            ("\uE749", "iconNames.printer"),
            ("\uE7E8", "iconNames.powerButton"),
            ("\uEBDE", "iconNames.deviceDiscovery"),
            ("\uE8AF", "iconNames.remote"),
            ("\uE772", "iconNames.devices"),
            ("\uE88E", "iconNames.usb"),
            ("\uEDA2", "iconNames.hardDrive"),
            ("\uEDA3", "iconNames.networkAdapter"),
            ("\uE7F1", "iconNames.sdCard"),
            ("\uE961", "iconNames.input"),
            ("\uE962", "iconNames.mouse"),
            ("\uEFA5", "iconNames.touchpad"),
            ("\uEDA4", "iconNames.touchscreen"),
            ("\uE765", "iconNames.keyboardClassic"),
            ("\uE957", "iconNames.sensor"),
            ("\uE95D", "iconNames.projector"),
            ("\uEEA0", "iconNames.ram"),
            ("\uEEA1", "iconNames.cpu"),
            ("\uEC94", "iconNames.hololens"),
            ("\uF847", "iconNames.eject"),
        ]),
        ("icons.category.systemAndApps", [
            ("\uE770", "iconNames.system"),
            ("\uE771", "iconNames.personalize"),
            ("\uE777", "iconNames.updateRestore"),
            ("\uE8F9", "iconNames.switchApps"),
            ("\uED35", "iconNames.apps"),
            ("\uEB3B", "iconNames.genericApp"),
            ("\uECAA", "iconNames.appIconDefault"),
            ("\uE7C4", "iconNames.taskView"),
            ("\uE748", "iconNames.switchUser"),
            ("\uEE3F", "iconNames.lockscreenDesktop"),
            ("\uF182", "iconNames.screenTime"),
            ("\uEF90", "iconNames.flow"),
            ("\uE9A3", "iconNames.aiSparkle"),
            ("\uE99A", "iconNames.robot"),
            ("\uF7EC", "iconNames.task"),
            ("\uE706", "iconNames.brightness"),
            ("\uE793", "iconNames.light"),
            ("\uF08C", "iconNames.blueLight"),
            ("\uE776", "iconNames.easeOfAccess"),
            ("\uE775", "iconNames.timeLanguage"),
            ("\uF2B7", "iconNames.localeLanguage"),
            ("\uE939", "iconNames.feedbackApp"),
            ("\uED15", "iconNames.feedback"),
            ("\uEEA3", "iconNames.virtualMachine"),
            ("\uEA86", "iconNames.puzzle"),
            ("\uF000", "iconNames.knowledgeArticle"),
            ("\uF246", "iconNames.viewDashboard"),
            ("\uF404", "iconNames.interactiveDashboard"),
            ("\uECA5", "iconNames.tiles"),
            ("\uF0E2", "iconNames.gridView"),
        ]),
        ("icons.category.securityAndPrivacy", [
            ("\uE72E", "iconNames.lock"),
            ("\uE785", "iconNames.unlock"),
            ("\uEA18", "iconNames.shield"),
            ("\uE83D", "iconNames.defender"),
            ("\uF5B4", "iconNames.shieldLock"),
            ("\uE727", "iconNames.inprivate"),
            ("\uE8D7", "iconNames.permissions"),
            ("\uE8D8", "iconNames.disableUpdates"),
            ("\uE730", "iconNames.reportHacked"),
            ("\uE733", "iconNames.blocked"),
            ("\uE928", "iconNames.fingerprint"),
            ("\uF439", "iconNames.dynamicLock"),
            ("\uF427", "iconNames.idBadge"),
            ("\uE963", "iconNames.smartcard"),
            ("\uEE7E", "iconNames.passkey"),
            ("\uE890", "iconNames.view"),
            ("\uED1A", "iconNames.hide"),
            ("\uE9A8", "iconNames.passwordShow"),
            ("\uE9A9", "iconNames.passwordHide"),
            ("\uF0EF", "iconNames.applicationGuard"),
            ("\uF540", "iconNames.safe"),
        ]),
        ("icons.category.navigationAndWindows", [
            ("\uE72A", "iconNames.forward"),
            ("\uE72B", "iconNames.back"),
            ("\uE70E", "iconNames.chevronUp"),
            ("\uE70D", "iconNames.chevronDown"),
            ("\uE76C", "iconNames.chevronRight"),
            ("\uE76B", "iconNames.chevronLeft"),
            ("\uE74A", "iconNames.up"),
            ("\uE74B", "iconNames.down"),
            ("\uE8AD", "iconNames.go"),
            ("\uE700", "iconNames.menu"),
            ("\uE712", "iconNames.more"),
            ("\uE71D", "iconNames.allApps"),
            ("\uE8FC", "iconNames.goToStart"),
            ("\uE89F", "iconNames.closePane"),
            ("\uE8A0", "iconNames.openPane"),
            ("\uE73F", "iconNames.backToWindow"),
            ("\uE740", "iconNames.fullScreen"),
            ("\uE97A", "iconNames.reply"),
            ("\uE8A9", "iconNames.viewAll"),
            ("\uE89A", "iconNames.twoPage"),
            ("\uE7C2", "iconNames.move"),
            ("\uE8B0", "iconNames.click"),
            ("\uECCD", "iconNames.exploreContent"),
        ]),
        ("icons.category.statusAndSymbols", [
            ("\uE734", "iconNames.star"),
            ("\uE735", "iconNames.starFilled"),
            ("\uE73E", "iconNames.checkmark"),
            ("\uEC61", "iconNames.completed"),
            ("\uE8FB", "iconNames.accept"),
            ("\uE7BA", "iconNames.warning"),
            ("\uE814", "iconNames.incidentTriangle"),
            ("\uE783", "iconNames.error"),
            ("\uE945", "iconNames.lightning"),
            ("\uE840", "iconNames.pinned"),
            ("\uE718", "iconNames.pin"),
            ("\uE77A", "iconNames.unpin"),
            ("\uE7C1", "iconNames.flag"),
            ("\uE946", "iconNames.info"),
            ("\uE897", "iconNames.help"),
            ("\uEB51", "iconNames.heart"),
            ("\uEB52", "iconNames.heartFill"),
            ("\uEA92", "iconNames.heartBroken"),
            ("\uE8E1", "iconNames.like"),
            ("\uE8E0", "iconNames.dislike"),
            ("\uEA80", "iconNames.lightbulb"),
            ("\uE91C", "iconNames.actionCenter"),
            ("\uEA8F", "iconNames.ringer"),
            ("\uE754", "iconNames.flashlight"),
            ("\uE781", "iconNames.ledLight"),
            ("\uE789", "iconNames.megaphone"),
            ("\uE8BE", "iconNames.leaf"),
            ("\uF1E8", "iconNames.leafTwo"),
            ("\uEC0A", "iconNames.sustainable"),
            ("\uEB42", "iconNames.drop"),
            ("\uE9CE", "iconNames.unknown"),
            ("\uEB44", "iconNames.radar"),
            ("\uE877", "iconNames.vibrate"),
            ("\uE915", "iconNames.radioBullet"),
            ("\uE9D2", "iconNames.areaChart"),
            ("\uEB05", "iconNames.pieChart"),
            ("\uF003", "iconNames.relationship"),
            ("\uEA24", "iconNames.beta"),
            ("\uE95E", "iconNames.health"),
        ]),
    ];

    // ═══════════════════════════════════════════════════════════
    //  Icon Data — Emoji
    // ═══════════════════════════════════════════════════════════

    private static readonly (string CategoryKey, (string Emoji, string NameKey)[] Items)[] EmojiCategories =
    [
        ("icons.category.smileysAndPeople", [
            ("😀", "iconNames.grinning"),
            ("😃", "iconNames.happy"),
            ("😄", "iconNames.bigSmile"),
            ("😁", "iconNames.beaming"),
            ("😆", "iconNames.laughing"),
            ("😅", "iconNames.sweatSmile"),
            ("🤣", "iconNames.rolling"),
            ("😂", "iconNames.joy"),
            ("🙂", "iconNames.slightSmile"),
            ("🙃", "iconNames.upsideDown"),
            ("😉", "iconNames.wink"),
            ("😊", "iconNames.blush"),
            ("😇", "iconNames.halo"),
            ("😎", "iconNames.cool"),
            ("🤓", "iconNames.nerd"),
            ("🧐", "iconNames.monocle"),
            ("🤩", "iconNames.starEyes"),
            ("😍", "iconNames.heartEyes"),
            ("🥰", "iconNames.smilingHearts"),
            ("😘", "iconNames.kiss"),
            ("😋", "iconNames.yummy"),
            ("😜", "iconNames.tongueWink"),
            ("🤪", "iconNames.zany"),
            ("😝", "iconNames.tongueSquint"),
            ("🥳", "iconNames.party"),
            ("🤗", "iconNames.hugging"),
            ("🤭", "iconNames.handOverMouth"),
            ("🤫", "iconNames.shushing"),
            ("🤔", "iconNames.thinking"),
            ("🤐", "iconNames.zipperMouth"),
            ("😏", "iconNames.smirk"),
            ("😌", "iconNames.relieved"),
            ("😔", "iconNames.pensive"),
            ("😪", "iconNames.sleepy"),
            ("😴", "iconNames.sleeping"),
            ("🤤", "iconNames.drooling"),
            ("😷", "iconNames.mask"),
            ("🤒", "iconNames.thermometer"),
            ("🤢", "iconNames.nauseated"),
            ("🤮", "iconNames.vomiting"),
            ("🥵", "iconNames.hot"),
            ("🥶", "iconNames.cold"),
            ("😵", "iconNames.dizzy"),
            ("🤯", "iconNames.explodingHead"),
            ("😱", "iconNames.screaming"),
            ("😨", "iconNames.fearful"),
            ("😰", "iconNames.anxious"),
            ("😢", "iconNames.crying"),
            ("😭", "iconNames.sobbing"),
            ("😤", "iconNames.huffing"),
            ("😠", "iconNames.angry"),
            ("😡", "iconNames.rage"),
            ("🤬", "iconNames.cursing"),
            ("🥺", "iconNames.pleading"),
            ("😬", "iconNames.grimacing"),
            ("🫠", "iconNames.melting"),
            ("🫡", "iconNames.saluting"),
            ("🫢", "iconNames.peeking"),
            ("🤖", "iconNames.robot"),
            ("👻", "iconNames.ghost"),
            ("💀", "iconNames.skull"),
            ("👽", "iconNames.alien"),
            ("🤡", "iconNames.clown"),
            ("😈", "iconNames.devil"),
            ("👹", "iconNames.ogre"),
            ("👺", "iconNames.goblin"),
            ("💩", "iconNames.poop"),
        ]),
        ("icons.category.gesturesAndBody", [
            ("👋", "iconNames.wave"),
            ("👍", "iconNames.thumbsUp"),
            ("👎", "iconNames.thumbsDown"),
            ("👏", "iconNames.clap"),
            ("🙏", "iconNames.pray"),
            ("💪", "iconNames.muscle"),
            ("🤝", "iconNames.handshake"),
            ("✌️", "iconNames.peace"),
            ("🤞", "iconNames.crossedFingers"),
            ("🤟", "iconNames.loveYou"),
            ("🤘", "iconNames.rockOn"),
            ("🤙", "iconNames.callMe"),
            ("👀", "iconNames.eyes"),
            ("👁️", "iconNames.eye"),
            ("🧠", "iconNames.brain"),
            ("🫀", "iconNames.anatomicalHeart"),
            ("🫁", "iconNames.lungs"),
            ("🦷", "iconNames.tooth"),
            ("🦴", "iconNames.bone"),
            ("👤", "iconNames.silhouette"),
            ("👥", "iconNames.twoSilhouettes"),
            ("🙋", "iconNames.raisingHand"),
            ("💁", "iconNames.tippingHand"),
            ("🤷", "iconNames.shrug"),
            ("🙇", "iconNames.bowing"),
            ("🙅", "iconNames.noGesture"),
            ("🙆", "iconNames.okGesture"),
            ("✋", "iconNames.raisedHand"),
            ("🤚", "iconNames.backOfHand"),
            ("🖐️", "iconNames.splayedHand"),
            ("🖖", "iconNames.vulcanSalute"),
            ("👆", "iconNames.pointUp"),
            ("👇", "iconNames.pointDown"),
            ("👈", "iconNames.pointLeft"),
            ("👉", "iconNames.pointRight"),
            ("☝️", "iconNames.indexUp"),
            ("🫵", "iconNames.pointingAtViewer"),
            ("✊", "iconNames.raisedFist"),
            ("👊", "iconNames.fistBump"),
            ("🤲", "iconNames.palmsUp"),
            ("🫶", "iconNames.heartHands"),
        ]),
        ("icons.category.animalsAndNature", [
            ("🐶", "iconNames.dog"),
            ("🐱", "iconNames.cat"),
            ("🐭", "iconNames.mouse"),
            ("🐹", "iconNames.hamster"),
            ("🐰", "iconNames.rabbit"),
            ("🦊", "iconNames.fox"),
            ("🐻", "iconNames.bear"),
            ("🐼", "iconNames.panda"),
            ("🐨", "iconNames.koala"),
            ("🐯", "iconNames.tiger"),
            ("🦁", "iconNames.lion"),
            ("🐮", "iconNames.cow"),
            ("🐷", "iconNames.pig"),
            ("🐸", "iconNames.frog"),
            ("🐵", "iconNames.monkey"),
            ("🐔", "iconNames.chicken"),
            ("🐧", "iconNames.penguin"),
            ("🐦", "iconNames.bird"),
            ("🦅", "iconNames.eagle"),
            ("🦆", "iconNames.duck"),
            ("🦉", "iconNames.owl"),
            ("🦇", "iconNames.bat"),
            ("🐺", "iconNames.wolf"),
            ("🐗", "iconNames.boar"),
            ("🐴", "iconNames.horse"),
            ("🦄", "iconNames.unicorn"),
            ("🐝", "iconNames.bee"),
            ("🐛", "iconNames.bug"),
            ("🦋", "iconNames.butterfly"),
            ("🐌", "iconNames.snail"),
            ("🐞", "iconNames.ladybug"),
            ("🐢", "iconNames.turtle"),
            ("🐍", "iconNames.snake"),
            ("🦎", "iconNames.lizard"),
            ("🐙", "iconNames.octopus"),
            ("🦑", "iconNames.squid"),
            ("🦐", "iconNames.shrimp"),
            ("🦀", "iconNames.crab"),
            ("🐠", "iconNames.tropicalFish"),
            ("🐡", "iconNames.blowfish"),
            ("🦈", "iconNames.shark"),
            ("🐬", "iconNames.dolphin"),
            ("🐳", "iconNames.whale"),
            ("🐊", "iconNames.crocodile"),
            ("🦕", "iconNames.dinosaur"),
            ("🦖", "iconNames.tRex"),
            ("🌲", "iconNames.tree"),
            ("🌳", "iconNames.deciduousTree"),
            ("🌴", "iconNames.palmTree"),
            ("🌵", "iconNames.cactus"),
            ("🌻", "iconNames.sunflower"),
            ("🌹", "iconNames.rose"),
            ("🌺", "iconNames.hibiscus"),
            ("🌸", "iconNames.cherryBlossom"),
            ("💐", "iconNames.bouquet"),
            ("🍀", "iconNames.clover"),
            ("🍁", "iconNames.mapleLeaf"),
            ("🍂", "iconNames.fallenLeaf"),
            ("🍃", "iconNames.leafInWind"),
            ("🌈", "iconNames.rainbow"),
            ("🌊", "iconNames.wave"),
            ("❄️", "iconNames.snowflake"),
            ("🌙", "iconNames.crescentMoon"),
            ("🌞", "iconNames.sun"),
            ("☀️", "iconNames.sunny"),
        ]),
        ("icons.category.foodAndDrink", [
            ("🍎", "iconNames.apple"),
            ("🍐", "iconNames.pear"),
            ("🍊", "iconNames.orange"),
            ("🍋", "iconNames.lemon"),
            ("🍌", "iconNames.banana"),
            ("🍉", "iconNames.watermelon"),
            ("🍇", "iconNames.grapes"),
            ("🍓", "iconNames.strawberry"),
            ("🫐", "iconNames.blueberries"),
            ("🍑", "iconNames.peach"),
            ("🥝", "iconNames.kiwi"),
            ("🍍", "iconNames.pineapple"),
            ("🥑", "iconNames.avocado"),
            ("🥕", "iconNames.carrot"),
            ("🌽", "iconNames.corn"),
            ("🌶️", "iconNames.pepper"),
            ("🍕", "iconNames.pizza"),
            ("🍔", "iconNames.burger"),
            ("🍟", "iconNames.fries"),
            ("🌮", "iconNames.taco"),
            ("🌯", "iconNames.burrito"),
            ("🍣", "iconNames.sushi"),
            ("🍜", "iconNames.steamingBowl"),
            ("🍝", "iconNames.spaghetti"),
            ("🥘", "iconNames.paella"),
            ("🍲", "iconNames.stew"),
            ("🥗", "iconNames.salad"),
            ("🍰", "iconNames.cake"),
            ("🎂", "iconNames.birthdayCake"),
            ("🍩", "iconNames.donut"),
            ("🍦", "iconNames.iceCream"),
            ("🍫", "iconNames.chocolate"),
            ("🍬", "iconNames.candy"),
            ("🍭", "iconNames.lollipop"),
            ("☕", "iconNames.coffee"),
            ("🍵", "iconNames.tea"),
            ("🍺", "iconNames.beer"),
            ("🍻", "iconNames.clinkingBeer"),
            ("🍷", "iconNames.wine"),
            ("🍸", "iconNames.cocktail"),
            ("🥂", "iconNames.champagne"),
            ("🧃", "iconNames.juiceBox"),
            ("🥤", "iconNames.cupWithStraw"),
            ("🧋", "iconNames.bubbleTea"),
            ("🍿", "iconNames.popcorn"),
            ("🧁", "iconNames.cupcake"),
            ("🍪", "iconNames.cookie"),
            ("🥐", "iconNames.croissant"),
            ("🥖", "iconNames.baguette"),
            ("🧀", "iconNames.cheese"),
            ("🥚", "iconNames.egg"),
            ("🥩", "iconNames.steak"),
            ("🍗", "iconNames.drumstick"),
        ]),
        ("icons.category.activitiesAndSports", [
            ("⚽", "iconNames.soccer"),
            ("🏀", "iconNames.basketball"),
            ("🏈", "iconNames.football"),
            ("⚾", "iconNames.baseball"),
            ("🎾", "iconNames.tennis"),
            ("🏐", "iconNames.volleyball"),
            ("🏓", "iconNames.pingPong"),
            ("🏸", "iconNames.badminton"),
            ("🥊", "iconNames.boxing"),
            ("⛳", "iconNames.golf"),
            ("🎱", "iconNames.billiards"),
            ("🏹", "iconNames.archery"),
            ("🎣", "iconNames.fishing"),
            ("🥋", "iconNames.martialArts"),
            ("⛷️", "iconNames.skiing"),
            ("🏂", "iconNames.snowboarding"),
            ("🏄", "iconNames.surfing"),
            ("🚴", "iconNames.cycling"),
            ("🏊", "iconNames.swimming"),
            ("🤸", "iconNames.cartwheeling"),
            ("🧗", "iconNames.climbing"),
            ("🧘", "iconNames.yoga"),
            ("🎮", "iconNames.videoGame"),
            ("🕹️", "iconNames.joystick"),
            ("🎲", "iconNames.dice"),
            ("🧩", "iconNames.puzzle"),
            ("♟️", "iconNames.chess"),
            ("🎯", "iconNames.bullseye"),
            ("🏆", "iconNames.trophy"),
            ("🥇", "iconNames.goldMedal"),
            ("🥈", "iconNames.silverMedal"),
            ("🥉", "iconNames.bronzeMedal"),
            ("🎪", "iconNames.circus"),
            ("🎭", "iconNames.theater"),
            ("🎨", "iconNames.art"),
            ("🖌️", "iconNames.paintbrush"),
            ("🎬", "iconNames.movie"),
            ("🎤", "iconNames.microphone"),
            ("🎧", "iconNames.headphones"),
            ("🎵", "iconNames.music"),
            ("🎶", "iconNames.notes"),
            ("🎸", "iconNames.guitar"),
            ("🎹", "iconNames.piano"),
            ("🥁", "iconNames.drum"),
            ("🎺", "iconNames.trumpet"),
            ("🎻", "iconNames.violin"),
            ("🎷", "iconNames.saxophone"),
        ]),
        ("icons.category.travelAndPlaces", [
            ("🚗", "iconNames.car"),
            ("🚕", "iconNames.taxi"),
            ("🚙", "iconNames.suv"),
            ("🚌", "iconNames.bus"),
            ("🚎", "iconNames.trolleybus"),
            ("🏎️", "iconNames.racingCar"),
            ("🚓", "iconNames.policeCar"),
            ("🚑", "iconNames.ambulance"),
            ("🚒", "iconNames.fireTruck"),
            ("🏍️", "iconNames.motorcycle"),
            ("🛵", "iconNames.scooter"),
            ("🚲", "iconNames.bicycle"),
            ("🛴", "iconNames.kickScooter"),
            ("🚂", "iconNames.locomotive"),
            ("🚆", "iconNames.train"),
            ("🚇", "iconNames.metro"),
            ("✈️", "iconNames.airplane"),
            ("🛩️", "iconNames.smallPlane"),
            ("🚀", "iconNames.rocket"),
            ("🛸", "iconNames.ufo"),
            ("🚁", "iconNames.helicopter"),
            ("⛵", "iconNames.sailboat"),
            ("🚢", "iconNames.ship"),
            ("🛥️", "iconNames.motorBoat"),
            ("⛽", "iconNames.fuelPump"),
            ("🏠", "iconNames.house"),
            ("🏡", "iconNames.houseGarden"),
            ("🏢", "iconNames.office"),
            ("🏬", "iconNames.departmentStore"),
            ("🏥", "iconNames.hospital"),
            ("🏫", "iconNames.school"),
            ("🏛️", "iconNames.classicalBuilding"),
            ("⛪", "iconNames.church"),
            ("🕌", "iconNames.mosque"),
            ("🕍", "iconNames.synagogue"),
            ("🏰", "iconNames.castle"),
            ("🗼", "iconNames.tower"),
            ("🗽", "iconNames.statueOfLiberty"),
            ("🌍", "iconNames.globeEarth"),
            ("🌎", "iconNames.globeAmericas"),
            ("🌏", "iconNames.globeAsia"),
            ("🏔️", "iconNames.mountain"),
            ("🏕️", "iconNames.camping"),
            ("🏖️", "iconNames.beach"),
            ("⛱️", "iconNames.beachUmbrella"),
            ("🌋", "iconNames.volcano"),
            ("🏜️", "iconNames.desert"),
            ("🌄", "iconNames.sunriseMountain"),
            ("🌅", "iconNames.sunrise"),
            ("🌆", "iconNames.cityscapeDusk"),
            ("🌇", "iconNames.sunset"),
            ("🌉", "iconNames.bridgeAtNight"),
        ]),
        ("icons.category.objectsAndTech", [
            ("💻", "iconNames.laptop"),
            ("🖥️", "iconNames.desktop"),
            ("⌨️", "iconNames.keyboard"),
            ("🖱️", "iconNames.mouse"),
            ("🖨️", "iconNames.printer"),
            ("📱", "iconNames.phone"),
            ("📲", "iconNames.mobileArrow"),
            ("📞", "iconNames.telephone"),
            ("📟", "iconNames.pager"),
            ("📠", "iconNames.fax"),
            ("📧", "iconNames.email"),
            ("📷", "iconNames.camera"),
            ("📸", "iconNames.cameraFlash"),
            ("📹", "iconNames.videoCamera"),
            ("🎥", "iconNames.movieCamera"),
            ("📺", "iconNames.television"),
            ("📻", "iconNames.radio"),
            ("🔧", "iconNames.wrench"),
            ("🛠️", "iconNames.hammerWrench"),
            ("🔨", "iconNames.hammer"),
            ("🪛", "iconNames.screwdriver"),
            ("🔩", "iconNames.nutAndBolt"),
            ("⚙️", "iconNames.gear"),
            ("🧲", "iconNames.magnet"),
            ("🔬", "iconNames.microscope"),
            ("🔭", "iconNames.telescope"),
            ("🩺", "iconNames.stethoscope"),
            ("💊", "iconNames.pill"),
            ("🩹", "iconNames.bandage"),
            ("💉", "iconNames.syringe"),
            ("💡", "iconNames.lightBulb"),
            ("🔦", "iconNames.flashlight"),
            ("🕯️", "iconNames.candle"),
            ("🪫", "iconNames.lowBattery"),
            ("🔋", "iconNames.battery"),
            ("🔌", "iconNames.plug"),
            ("📚", "iconNames.books"),
            ("📖", "iconNames.book"),
            ("📰", "iconNames.newspaper"),
            ("📝", "iconNames.memo"),
            ("📋", "iconNames.clipboard"),
            ("📎", "iconNames.paperclip"),
            ("🖇️", "iconNames.linkedPaperclips"),
            ("📐", "iconNames.triangularRuler"),
            ("📏", "iconNames.straightRuler"),
            ("✂️", "iconNames.scissors"),
            ("🗂️", "iconNames.cardIndex"),
            ("🗃️", "iconNames.cardFileBox"),
            ("🗄️", "iconNames.fileCabinet"),
            ("🗑️", "iconNames.wastebasket"),
            ("🔑", "iconNames.key"),
            ("🗝️", "iconNames.oldKey"),
            ("🏷️", "iconNames.label"),
            ("💾", "iconNames.floppyDisk"),
            ("💿", "iconNames.cd"),
            ("📀", "iconNames.dvd"),
            ("📦", "iconNames.package"),
            ("🎁", "iconNames.gift"),
            ("🛒", "iconNames.shoppingCart"),
            ("🪴", "iconNames.pottedPlant"),
            ("🧸", "iconNames.teddyBear"),
            ("🪞", "iconNames.mirror"),
            ("🪟", "iconNames.window"),
            ("🛏️", "iconNames.bed"),
            ("🪑", "iconNames.chair"),
            ("🚿", "iconNames.shower"),
            ("🛁", "iconNames.bathtub"),
            ("🧹", "iconNames.broom"),
            ("🧺", "iconNames.basket"),
            ("👓", "iconNames.glasses"),
            ("🕶️", "iconNames.sunglasses"),
            ("👔", "iconNames.necktie"),
            ("👕", "iconNames.tShirt"),
            ("👗", "iconNames.dress"),
            ("👑", "iconNames.crown"),
            ("🎒", "iconNames.backpack"),
            ("👜", "iconNames.handbag"),
            ("💼", "iconNames.briefcase"),
            ("🧳", "iconNames.luggage"),
            ("☂️", "iconNames.umbrella"),
        ]),
        ("icons.category.symbolsAndShapes", [
            ("❤️", "iconNames.redHeart"),
            ("🧡", "iconNames.orangeHeart"),
            ("💛", "iconNames.yellowHeart"),
            ("💚", "iconNames.greenHeart"),
            ("💙", "iconNames.blueHeart"),
            ("💜", "iconNames.purpleHeart"),
            ("🖤", "iconNames.blackHeart"),
            ("🤍", "iconNames.whiteHeart"),
            ("🤎", "iconNames.brownHeart"),
            ("💔", "iconNames.brokenHeart"),
            ("❣️", "iconNames.heartExclamation"),
            ("💕", "iconNames.twoHearts"),
            ("💞", "iconNames.revolvingHearts"),
            ("💓", "iconNames.beatingHeart"),
            ("💗", "iconNames.growingHeart"),
            ("💖", "iconNames.sparklingHeart"),
            ("💘", "iconNames.heartArrow"),
            ("💝", "iconNames.heartRibbon"),
            ("💯", "iconNames.hundred"),
            ("⭐", "iconNames.star"),
            ("🌟", "iconNames.glowingStar"),
            ("✨", "iconNames.sparkles"),
            ("⚡", "iconNames.lightning"),
            ("🔥", "iconNames.fire"),
            ("💧", "iconNames.water"),
            ("🌀", "iconNames.cyclone"),
            ("☁️", "iconNames.cloud"),
            ("🌤️", "iconNames.sunBehindCloud"),
            ("⛈️", "iconNames.thunderstorm"),
            ("🌧️", "iconNames.rain"),
            ("🌨️", "iconNames.snow"),
            ("🌪️", "iconNames.tornado"),
            ("🔒", "iconNames.lock"),
            ("🔓", "iconNames.unlock"),
            ("🔐", "iconNames.lockWithKey"),
            ("⏰", "iconNames.alarmClock"),
            ("⏱️", "iconNames.stopwatch"),
            ("⏳", "iconNames.hourglass"),
            ("🔔", "iconNames.bell"),
            ("🔕", "iconNames.bellSlash"),
            ("📌", "iconNames.pin"),
            ("📍", "iconNames.roundPin"),
            ("✅", "iconNames.check"),
            ("❌", "iconNames.cross"),
            ("❓", "iconNames.question"),
            ("❗", "iconNames.exclamation"),
            ("⚠️", "iconNames.warning"),
            ("🚫", "iconNames.prohibited"),
            ("⛔", "iconNames.noEntry"),
            ("♻️", "iconNames.recycling"),
            ("💎", "iconNames.gem"),
            ("💰", "iconNames.moneyBag"),
            ("💵", "iconNames.dollar"),
            ("💳", "iconNames.creditCard"),
            ("🏧", "iconNames.atm"),
            ("🔴", "iconNames.redCircle"),
            ("🟠", "iconNames.orangeCircle"),
            ("🟡", "iconNames.yellowCircle"),
            ("🟢", "iconNames.greenCircle"),
            ("🔵", "iconNames.blueCircle"),
            ("🟣", "iconNames.purpleCircle"),
            ("⚫", "iconNames.blackCircle"),
            ("⚪", "iconNames.whiteCircle"),
            ("🟤", "iconNames.brownCircle"),
            ("🔺", "iconNames.redTriangleUp"),
            ("🔻", "iconNames.redTriangleDown"),
            ("🔶", "iconNames.largeOrangeDiamond"),
            ("🔷", "iconNames.largeBlueDiamond"),
            ("▶️", "iconNames.play"),
            ("⏸️", "iconNames.pause"),
            ("⏹️", "iconNames.stop"),
            ("⏭️", "iconNames.nextTrack"),
            ("🔀", "iconNames.shuffle"),
            ("🔁", "iconNames.repeat"),
            ("🔃", "iconNames.clockwiseArrows"),
            ("➕", "iconNames.plus"),
            ("➖", "iconNames.minus"),
            ("✖️", "iconNames.multiply"),
            ("➗", "iconNames.divide"),
            ("♾️", "iconNames.infinity"),
            ("💬", "iconNames.speechBubble"),
            ("💭", "iconNames.thoughtBubble"),
            ("🗯️", "iconNames.angerBubble"),
            ("ℹ️", "iconNames.info"),
            ("🆕", "iconNames.new"),
            ("🆗", "iconNames.ok"),
            ("🆘", "iconNames.sos"),
            ("🏳️", "iconNames.whiteFlag"),
            ("🏴", "iconNames.blackFlag"),
            ("🚩", "iconNames.redFlag"),
            ("🏁", "iconNames.checkeredFlag"),
        ]),
    ];
}
