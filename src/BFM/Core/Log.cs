namespace Bfm.Core;

internal static class Log
{
    const long MaxBytes = 256 * 1024;
    static readonly object Gate = new();
    static string FilePath => Path.Combine(Settings.Folder, "juicebar.log");

    /// <summary>Set by <c>--probe</c>: diagnostic detail goes to the console.</summary>
    public static bool Verbose { get; set; }

    public static void Debug(string message)
    {
        if (Verbose) Console.WriteLine("    . " + message);
    }

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Settings.Folder);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxBytes)
                    info.Delete();
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
            catch
            {
            }
        }
    }
}
