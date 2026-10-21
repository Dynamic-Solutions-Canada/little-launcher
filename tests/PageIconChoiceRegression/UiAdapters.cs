// These adapters exercise production async/persistence code without loading WinUI on Linux.
// Image rendering remains a Windows acceptance check.
namespace Microsoft.UI.Xaml
{
    internal enum Visibility { Visible, Collapsed }
    internal enum TextWrapping { Wrap }
    internal enum VerticalAlignment { Center }
    internal enum HorizontalAlignment { Stretch }
    internal enum GridUnitType { Star }
    internal readonly record struct Thickness(double Left, double Top, double Right, double Bottom)
    {
        internal Thickness(double all) : this(all, all, all, all) { }
    }
    internal readonly record struct GridLength(double Value, GridUnitType Unit)
    {
        internal static GridLength Auto => default;
    }
    internal class FrameworkElement
    {
        public double Width { get; set; }
        public double Height { get; set; }
        public VerticalAlignment VerticalAlignment { get; set; }
        public HorizontalAlignment HorizontalAlignment { get; set; }
        public Visibility Visibility { get; set; }
    }
    internal sealed class Application
    {
        internal static Application Current { get; } = new();
        internal ResourceDictionary Resources { get; } = new();
    }
    internal sealed class ResourceDictionary
    {
        public object this[string key] => key;
    }
}

namespace Microsoft.UI.Text
{
    internal static class FontWeights
    {
        internal static object Medium => new();
    }
}

namespace Microsoft.UI.Xaml.Automation
{
    internal static class AutomationProperties
    {
        internal static void SetName(object element, string name) { }
    }
}

namespace Microsoft.UI.Xaml.Controls
{
    internal class Grid : FrameworkElement
    {
        internal double ColumnSpacing { get; set; }
        internal List<ColumnDefinition> ColumnDefinitions { get; } = [];
        internal List<FrameworkElement> Children { get; } = [];
        internal static void SetColumn(object element, int column) { }
    }
    internal sealed class ColumnDefinition
    {
        internal GridLength Width { get; set; }
    }
    internal sealed class StackPanel : FrameworkElement
    {
        internal List<FrameworkElement> Children { get; } = [];
    }
    internal sealed class Image : FrameworkElement
    {
        internal object? Source { get; set; }
    }
    internal sealed class ProgressRing : FrameworkElement
    {
        internal bool IsActive { get; set; }
    }
    internal sealed class TextBlock : FrameworkElement
    {
        internal double FontSize { get; set; }
        internal double Opacity { get; set; }
        internal TextWrapping TextWrapping { get; set; }
        internal string Text { get; set; } = "";
        internal object? FontWeight { get; set; }
    }
    internal sealed class Button : FrameworkElement
    {
        internal object? Content { get; set; }
        internal HorizontalAlignment HorizontalContentAlignment { get; set; }
        internal Thickness Padding { get; set; }
        internal Thickness BorderThickness { get; set; }
        internal bool IsEnabled { get; set; } = true;
        internal event EventHandler? Click;
        internal void RaiseClick() => Click?.Invoke(this, EventArgs.Empty);
    }
}

namespace Microsoft.UI.Xaml.Media.Imaging
{
    internal enum BitmapCreateOptions { IgnoreImageCache }
    internal sealed class BitmapImage
    {
        internal BitmapCreateOptions CreateOptions { get; set; }
        internal int DecodePixelWidth { get; set; }
        internal byte[] Bytes { get; private set; } = [];
        internal Task SetSourceAsync(global::Windows.Storage.Streams.InMemoryRandomAccessStream stream)
        {
            Bytes = stream.Buffer.ToArray();
            return Task.CompletedTask;
        }
    }
}

namespace Windows.Storage.Streams
{
    internal sealed class InMemoryRandomAccessStream : IDisposable
    {
        internal MemoryStream Buffer { get; } = new();
        internal void Seek(ulong position) => Buffer.Position = (long)position;
        public void Dispose() => Buffer.Dispose();
    }
    internal sealed class DataWriter(InMemoryRandomAccessStream stream) : IDisposable
    {
        internal void WriteBytes(byte[] bytes) => stream.Buffer.Write(bytes);
        internal Task StoreAsync() => Task.CompletedTask;
        internal void DetachStream() { }
        public void Dispose() { }
    }
}

namespace NLog
{
    internal static class LogManager
    {
        internal static Logger GetCurrentClassLogger() => new();
    }
    internal sealed class Logger
    {
        internal void Debug(Exception exception, string message) { }
    }
}
