using System.Buffers.Binary;
using System.IO.Compression;
using LittleLauncher.Classes;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

internal static class Program
{
    private const string Url = "https://example.invalid/notes";
    private static readonly byte[] Saved = CreatePng(512, transparent: false);
    private static readonly byte[] Retried = CreatePng(32, transparent: true);

    private static async Task Main()
    {
        string directory = Path.Combine(Path.GetTempPath(), "page-icon-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "web-favicon-test.png");
            await ConfirmRoundTripAsync(path);
            await CancelAsync(path);
            await FailedRetryAsync(path);
            await ChangedSourceAsync(path, "address");
            await ChangedSourceAsync(path, "launcher type");
            await DisposeDuringRetryAsync(path);
            EmptyPreview(path);
            Console.WriteLine("PASS: exact Retry/Confirm/reload, Cancel, failed Retry, stale address/type, disposal, empty preview");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Exercises the production preview snapshot and atomic writer, including a smaller replacement.</summary>
    private static async Task ConfirmRoundTripAsync(string path)
    {
        File.WriteAllBytes(path, Saved);
        using var choice = Choice(path, _ => Task.FromResult<byte[]?>(Retried));
        await choice.LoadCachedAsync();
        Equal(Saved, choice.Preview.Png, "Opening must restore the saved bytes");
        await RetryAsync(choice);
        Equal(Retried, choice.Preview.Png, "Retry must publish the new bytes");
        Equal(Retried, DisplayedBytes(choice), "The pending bytes must match the displayed image");
        Equal(Saved, File.ReadAllBytes(path), "Retry must not write the managed file");

        // The gallery snapshots before Hide/Closed disposes its choice, then invokes its caller.
        var confirmed = choice.Preview;
        choice.Dispose();
        Require(confirmed.Store(path) == path, "Confirm must preserve the managed automatic identity");
        Equal(Retried, File.ReadAllBytes(path), "Confirm must write the exact smaller transparent preview");
        Require(!Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp").Any(), "No staging files may remain");

        // Reload from the persisted path, as a newly opened gallery/app does, without fetching.
        using var reopened = Choice(path, _ => throw new Exception("Opening must never fetch"));
        await reopened.LoadCachedAsync();
        Equal(Retried, reopened.Preview.Png, "Reload must retain the confirmed image");
        Equal(Retried, DisplayedBytes(reopened), "Reload must display the confirmed bytes");
    }

    private static async Task CancelAsync(string path)
    {
        File.WriteAllBytes(path, Saved);
        using var choice = Choice(path, _ => Task.FromResult<byte[]?>(Retried));
        await choice.LoadCachedAsync();
        await RetryAsync(choice);
        choice.Dispose();
        Equal(Saved, File.ReadAllBytes(path), "Cancel after Retry must preserve the saved image");
        using var reopened = Choice(path, _ => throw new Exception("Opening must never fetch"));
        await reopened.LoadCachedAsync();
        Equal(Saved, reopened.Preview.Png, "Reopening after Cancel must restore the saved image");
    }

    private static async Task FailedRetryAsync(string path)
    {
        File.WriteAllBytes(path, Saved);
        using var choice = Choice(path, _ => Task.FromResult<byte[]?>(null));
        await choice.LoadCachedAsync();
        await RetryAsync(choice);
        Equal(Saved, choice.Preview.Png, "A failed Retry must retain the last good preview");
        Equal(Saved, DisplayedBytes(choice), "A failed Retry must retain the displayed image");
        Require(choice.SelectButton.IsEnabled, "A failed Retry must leave the saved image selectable");
    }

    /// <summary>Completes an old request after its URL/kind predicate changes, without any file mutation.</summary>
    private static async Task ChangedSourceAsync(string path, string change)
    {
        File.WriteAllBytes(path, Saved);
        bool current = true;
        var completion = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var choice = Choice(path, _ => completion.Task, () => current);
        await choice.LoadCachedAsync();
        RetryButton(choice).RaiseClick();
        current = false;
        completion.SetResult(Retried);
        await UntilAsync(() => !Progress(choice).IsActive);
        Equal(Saved, choice.Preview.Png, $"Changed {change} must reject late preview bytes");
        Equal(Saved, File.ReadAllBytes(path), $"Changed {change} must preserve the saved image");
        Require(!choice.SelectButton.IsEnabled, $"Changed {change} must disable the stale choice");
        Require(!RetryButton(choice).IsEnabled, $"Changed {change} must disable stale Retry");
    }

    private static async Task DisposeDuringRetryAsync(string path)
    {
        File.WriteAllBytes(path, Saved);
        CancellationToken token = default;
        var completion = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var choice = Choice(path, cancellation =>
        {
            token = cancellation;
            return completion.Task;
        });
        await choice.LoadCachedAsync();
        RetryButton(choice).RaiseClick();
        choice.Dispose();
        Require(token.IsCancellationRequested, "Close must cancel the pending request");
        Require(!Progress(choice).IsActive, "Close must stop its loading indicator");
        completion.SetResult(Retried);
        await Task.Delay(30);
        Equal(Saved, choice.Preview.Png, "A completion after close must not publish bytes");
        Equal(Saved, File.ReadAllBytes(path), "A completion after close must not write bytes");
    }

    private static void EmptyPreview(string path)
    {
        File.WriteAllBytes(path, Saved);
        var empty = new PageIconPreview(Url, null);
        Require(empty.Store(path) == path, "An empty preview must retain an existing automatic file");
        Equal(Saved, File.ReadAllBytes(path), "An empty preview must not modify an existing file");
        File.Delete(path);
        Require(empty.Store(path) == "", "Without any file, Confirm must allow future automatic adoption");
        Require(!File.Exists(path), "An empty preview must not create an image file");
    }

    private static AutomaticPageIconChoice Choice(string path, Func<CancellationToken, Task<byte[]?>> fetch,
        Func<bool>? isCurrent = null) =>
        new(new PageIconPreviewSource(Url, path, fetch, isCurrent ?? (() => true)), () => { }, () => { });

    private static Button RetryButton(AutomaticPageIconChoice choice) =>
        choice.Children.OfType<Button>().Single(button => button != choice.SelectButton);

    private static Grid ImageHost(AutomaticPageIconChoice choice) =>
        ((Grid)choice.SelectButton.Content!).Children.OfType<Grid>().Single();

    private static ProgressRing Progress(AutomaticPageIconChoice choice) => ImageHost(choice).Children.OfType<ProgressRing>().Single();

    private static byte[] DisplayedBytes(AutomaticPageIconChoice choice) =>
        ((BitmapImage)ImageHost(choice).Children.OfType<Image>().Single().Source!).Bytes;

    private static async Task RetryAsync(AutomaticPageIconChoice choice)
    {
        RetryButton(choice).RaiseClick();
        await UntilAsync(() => RetryButton(choice).IsEnabled);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition())
        {
            await Task.Delay(5, timeout.Token);
        }
    }

    private static void Equal(byte[] expected, byte[]? actual, string message) =>
        Require(actual != null && expected.SequenceEqual(actual), message);

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception(message);
        }
    }

    /// <summary>Builds valid RGBA PNGs with visibly different backgrounds and dimensions, using only the BCL.</summary>
    private static byte[] CreatePng(int size, bool transparent)
    {
        using var png = new MemoryStream();
        png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, size);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), size);
        header[8] = 8;
        header[9] = 6;
        Chunk("IHDR", header);
        using var pixels = new MemoryStream();
        using (var compressed = new ZLibStream(pixels, CompressionLevel.Fastest, leaveOpen: true))
        {
            for (int y = 0; y < size; y++)
            {
                compressed.WriteByte(0);
                for (int x = 0; x < size; x++)
                {
                    bool ink = x >= size / 4 && x < size * 3 / 4;
                    compressed.Write(new byte[] { ink ? (byte)0 : (byte)255, 255, 255, transparent && !ink ? (byte)0 : (byte)255 });
                }
            }
        }
        Chunk("IDAT", pixels.ToArray());
        Chunk("IEND", []);
        return png.ToArray();

        void Chunk(string name, byte[] bytes)
        {
            var length = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            png.Write(length);
            var type = System.Text.Encoding.ASCII.GetBytes(name);
            png.Write(type);
            png.Write(bytes);
            uint crc = uint.MaxValue;
            foreach (byte value in type.Concat(bytes))
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++)
                {
                    crc = (crc >> 1) ^ ((crc & 1) == 1 ? 0xedb88320u : 0);
                }
            }
            BinaryPrimitives.WriteUInt32BigEndian(length, ~crc);
            png.Write(length);
        }
    }
}
