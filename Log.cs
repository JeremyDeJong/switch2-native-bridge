namespace Switch2Bridge;

/// <summary>Writes to the log file, to stdout (self-test, console runs) and to any
/// open log window. The file lives in %LOCALAPPDATA%\Switch2Bridge.</summary>
static class Log
{
    public static readonly string Directory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Switch2Bridge");
    public static readonly string FilePath = Path.Combine(Directory, "bridge.log");

    const int MaxFileBytes = 1 << 20;
    const int KeepLines = 500;

    static readonly object Gate = new();
    static readonly LinkedList<string> Recent = new();

    /// <summary>Raised with each formatted line, on the logging thread.</summary>
    public static event Action<string>? Line;

    static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}";
        lock (Gate)
        {
            Recent.AddLast(line);
            if (Recent.Count > KeepLines) Recent.RemoveFirst();
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxFileBytes)
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            try { Console.WriteLine(line); } catch (IOException) { }
        }
        Line?.Invoke(line);
    }

    public static string[] Snapshot()
    {
        lock (Gate) return Recent.ToArray();
    }

    public static void Info(string m) => Write("INFO", m);
    public static void Ok(string m) => Write(" OK ", m);
    public static void Warn(string m) => Write("WARN", m);
    public static void Error(string m) => Write("FAIL", m);
}
