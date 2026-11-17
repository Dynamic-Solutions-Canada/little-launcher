// Only Windows-protected token storage and logging are adapted; the OAuth flow is production code.
// The stand-ins below sit in production namespaces on purpose; without this, dotnet format's
// namespace-matches-folder fix (IDE0130) tries to move the file and crashes.
#pragma warning disable IDE0130
namespace NLog
{
    public class Logger
    {
        public void Debug(Exception exception, string message) { }
        public void Debug(string message) { }
        public void Warn(string message) { }
    }

    public static class LogManager
    {
        public static Logger GetCurrentClassLogger() => new();
    }
}

namespace LittleLauncher.Services
{
    /// <summary>In-memory stand-in for the DPAPI-backed store, so nothing touches real credentials.</summary>
    public static class ProtectedStore
    {
        private static readonly Dictionary<string, object> Values = [];

        public static T? Load<T>(string name) where T : class => Values.TryGetValue(name, out var value) ? (T)value : null;

        public static void Save<T>(string name, T value) => Values[name] = value!;

        public static void Clear(string name) => Values.Remove(name);
    }
}
#pragma warning restore IDE0130
