using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Switch2Bridge;

sealed record UpdateInfo(Version Version, string Tag, string PageUrl, string ExeName, string ExeUrl, string ShaUrl);

/// <summary>
/// Checks this project's GitHub Releases for a newer version and, when the user asks,
/// downloads it, checks it against the release's .sha256 file, starts it and exits.
/// The new exe sits beside the old one under its own versioned name; it waits for this
/// process to exit, then deletes the old exe (see Program, --after-update).
/// </summary>
static class Updater
{
    public const string Repository = "JeremyDeJong/switch2-native-bridge";
    static readonly Regex ExeAsset = new(@"^Switch2Bridge-v[\d.]+\.exe$", RegexOptions.IgnoreCase);

    public static Version Current => typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0);

    static HttpClient NewClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Switch2Bridge/{Current.ToString(3)}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>The latest release if it is newer than this build and has both files; else null.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct)
    {
        using var http = NewClient();
        using var response = await http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // no releases yet
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (ParseVersion(tag) is not Version version || version <= Current) return null;

        string? exeName = null, exeUrl = null, shaUrl = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            var url = asset.GetProperty("browser_download_url").GetString() ?? "";
            if (ExeAsset.IsMatch(name)) { exeName = name; exeUrl = url; }
        }
        if (exeName != null)
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
                if (asset.GetProperty("name").GetString() == exeName + ".sha256")
                    shaUrl = asset.GetProperty("browser_download_url").GetString();
        if (exeName == null || exeUrl == null || shaUrl == null)
        {
            Log.Warn($"Release {tag} has no Switch2Bridge-v*.exe with a .sha256 beside it; not offering it.");
            return null;
        }
        return new UpdateInfo(version, tag, root.GetProperty("html_url").GetString() ?? "", exeName, exeUrl, shaUrl);
    }

    internal static Version? ParseVersion(string tag)
    {
        var m = Regex.Match(tag, @"(\d+)\.(\d+)(?:\.(\d+))?");
        if (!m.Success) return null;
        return new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
            m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
    }

    /// <summary>Downloads and verifies the update next to this exe, starts it, and returns
    /// once it is running; the caller then exits.</summary>
    public static async Task InstallAsync(UpdateInfo update, IProgress<string> progress, CancellationToken ct)
    {
        var current = Environment.ProcessPath ?? throw new InvalidOperationException("Can't find this exe's path.");
        var folder = Path.GetDirectoryName(current)!;
        var target = Path.Combine(folder, update.ExeName);
        var partial = target + ".download";

        using var http = NewClient();
        progress.Report("Downloading checksum...");
        var shaText = await http.GetStringAsync(update.ShaUrl, ct);
        var expected = shaText.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant();
        if (expected is not { Length: 64 }) throw new InvalidOperationException("The release's .sha256 file is malformed.");

        progress.Report($"Downloading {update.ExeName}...");
        await using (var src = await http.GetStreamAsync(update.ExeUrl, ct))
        await using (var dst = File.Create(partial))
            await src.CopyToAsync(dst, ct);

        progress.Report("Checking the download...");
        string actual;
        await using (var file = File.OpenRead(partial))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).ToLowerInvariant();
        if (actual != expected)
        {
            File.Delete(partial);
            throw new InvalidOperationException("The download doesn't match the release's checksum, so it wasn't installed.");
        }

        if (!string.Equals(target, current, StringComparison.OrdinalIgnoreCase))
            File.Move(partial, target, overwrite: true);
        else
            throw new InvalidOperationException("The update has the same file name as the running exe.");

        Log.Ok($"Downloaded and verified {update.ExeName}; starting it.");
        progress.Report("Starting the new version...");
        var psi = new ProcessStartInfo(target) { UseShellExecute = false, WorkingDirectory = folder };
        psi.ArgumentList.Add("--after-update");
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        psi.ArgumentList.Add(current);
        Process.Start(psi);
    }

    /// <summary>Run by the new version: waits for the old process, then deletes its exe
    /// if it is a versioned bridge exe in the same folder.</summary>
    public static void FinishUpdate(int oldPid, string oldExe)
    {
        try { using var old = Process.GetProcessById(oldPid); old.WaitForExit(20_000); }
        catch (ArgumentException) { } // already gone

        var me = Environment.ProcessPath ?? "";
        bool sameFolder = string.Equals(Path.GetDirectoryName(oldExe), Path.GetDirectoryName(me), StringComparison.OrdinalIgnoreCase);
        if (!sameFolder || !ExeAsset.IsMatch(Path.GetFileName(oldExe)) || string.Equals(oldExe, me, StringComparison.OrdinalIgnoreCase))
            return;
        for (int i = 0; i < 10; i++)
        {
            try { File.Delete(oldExe); Log.Info($"Removed the previous version, {Path.GetFileName(oldExe)}."); return; }
            catch (IOException) { Thread.Sleep(500); }
            catch (UnauthorizedAccessException) { Thread.Sleep(500); }
        }
        Log.Warn($"Couldn't remove {oldExe}; delete it yourself when convenient.");
    }
}
