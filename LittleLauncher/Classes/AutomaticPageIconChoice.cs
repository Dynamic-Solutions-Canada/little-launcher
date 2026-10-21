using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using global::Windows.Storage.Streams;

namespace LittleLauncher.Classes;

/// <summary>Non-mutating preview inputs. IsCurrent rejects an address/type change without starting a browser.</summary>
internal sealed record PageIconPreviewSource(
    string Url, string CachedPath, Func<CancellationToken, Task<byte[]?>> RetryAsync, Func<bool> IsCurrent);

/// <summary>
/// An automatic page-icon option with an actual decoded preview and independent Retry action.
/// Fetches affect only this choice's memory; callers receive bytes to adopt only after Confirm.
/// </summary>
internal sealed class AutomaticPageIconChoice : Grid, IDisposable
{
    private readonly PageIconPreviewSource _source;
    private readonly Action _onPreviewChanged;
    private readonly Image _image = new() { Width = 36, Height = 36, Visibility = Visibility.Collapsed };
    private readonly ProgressRing _progress = new() { Width = 24, Height = 24, IsActive = false };
    private readonly TextBlock _status = new() { FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
    private readonly Button _retry;
    private CancellationTokenSource? _request;
    private byte[]? _png;
    private int _version;
    private bool _disposed;

    internal Button SelectButton { get; }
    internal PageIconPreview Preview => new(_source.Url, _png);

    internal AutomaticPageIconChoice(PageIconPreviewSource source, Action onSelected, Action onPreviewChanged)
    {
        _source = source;
        _onPreviewChanged = onPreviewChanged;
        ColumnSpacing = 8;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var preview = new Grid { Width = 40, Height = 40, VerticalAlignment = VerticalAlignment.Center };
        preview.Children.Add(_image);
        preview.Children.Add(_progress);
        var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(new TextBlock
        {
            Text = Text("PageIconChoiceLabel"), FontWeight = Microsoft.UI.Text.FontWeights.Medium,
            TextWrapping = TextWrapping.Wrap
        });
        label.Children.Add(_status);
        var content = new Grid { ColumnSpacing = 10 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.Children.Add(preview);
        Grid.SetColumn(label, 1);
        content.Children.Add(label);
        SelectButton = new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(12, 8, 12, 8), BorderThickness = new Thickness(0)
        };
        AutomationProperties.SetName(SelectButton, Text("PageIconChoiceLabel"));
        SelectButton.Click += (_, _) => onSelected();
        Children.Add(SelectButton);
        _retry = new Button { Content = Text("PageIconRetry"), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(_retry, Text("PageIconRetryAccessibleName"));
        _retry.Click += async (_, _) => await LoadAsync(retry: true);
        Grid.SetColumn(_retry, 1);
        Children.Add(_retry);
        _status.Text = Text("PageIconMissing");
    }

    /// <summary>Loads the existing managed file only; opening the chooser never fetches or starts a browser.</summary>
    internal Task LoadCachedAsync() => LoadAsync(retry: false);

    /// <summary>Invalidates pending decoding/fetching on close; old completions cannot update a reopened choice.</summary>
    public void Dispose()
    {
        _disposed = true;
        _version++;
        _request?.Cancel();
        _request?.Dispose();
        _request = null;
        _progress.IsActive = false;
    }

    private bool IsCurrent(int version) => !_disposed && version == _version && _source.IsCurrent();

    /// <summary>Keeps the last good preview on failure and updates neither launcher fields nor settings.</summary>
    private async Task LoadAsync(bool retry)
    {
        if (_disposed || !_source.IsCurrent())
        {
            return;
        }
        _request?.Cancel();
        _request?.Dispose();
        using var request = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _request = request;
        int version = ++_version;
        _retry.IsEnabled = false;
        _progress.IsActive = true;
        _status.Text = Text("PageIconLoading");
        try
        {
            byte[]? bytes;
            if (retry)
            {
                bytes = await _source.RetryAsync(request.Token).WaitAsync(request.Token);
            }
            else
            {
                bytes = File.Exists(_source.CachedPath)
                    ? await File.ReadAllBytesAsync(_source.CachedPath, request.Token)
                    : null;
            }
            if (!IsCurrent(version))
            {
                return;
            }
            if (bytes == null || bytes.Length == 0)
            {
                _status.Text = Text(retry ? "PageIconRetryFailed" : "PageIconMissing");
                return;
            }
            await DecodePreviewAsync(bytes, version, request.Token);
            if (IsCurrent(version))
            {
                _status.Text = Text(retry ? "PageIconReady" : "PageIconSavedPreview");
            }
        }
        catch (OperationCanceledException)
        {
            if (IsCurrent(version))
            {
                _status.Text = Text("PageIconRetryFailed");
            }
        }
        catch (Exception ex)
        {
            NLog.LogManager.GetCurrentClassLogger().Debug(ex, "Loading the automatic page-icon preview failed");
            if (IsCurrent(version))
            {
                _status.Text = Text("PageIconRetryFailed");
            }
        }
        finally
        {
            if (ReferenceEquals(_request, request))
            {
                _request = null;
            }
            // A changed source rejects results, but this generation still owns its loading
            // indicator. Only a newer request or disposal may take cleanup away from it.
            if (!_disposed && version == _version)
            {
                _progress.IsActive = false;
                bool sourceCurrent = _source.IsCurrent();
                _retry.IsEnabled = sourceCurrent;
                if (!sourceCurrent)
                {
                    _status.Text = Text("PageIconAddressChanged");
                    SelectButton.IsEnabled = false;
                }
            }
        }
    }

    /// <summary>Decodes bytes before marking success; a missing/invalid image is never presented as a successful globe.</summary>
    private async Task DecodePreviewAsync(byte[] bytes, int version, CancellationToken cancellationToken)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            writer.DetachStream();
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCurrent(version))
        {
            return;
        }
        stream.Seek(0);
        var bitmap = new BitmapImage
        {
            CreateOptions = BitmapCreateOptions.IgnoreImageCache,
            DecodePixelWidth = 48
        };
        await bitmap.SetSourceAsync(stream);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCurrent(version))
        {
            return;
        }
        _png = bytes;
        _image.Source = bitmap;
        _image.Visibility = Visibility.Visible;
        _onPreviewChanged();
    }

    private static string Text(string key) => (string)Application.Current.Resources[key];
}
