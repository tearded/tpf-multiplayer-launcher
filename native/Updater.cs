using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Linq;
using System.Drawing;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

public class Asset {
    public string name { get; set; }
    public string browser_download_url { get; set; }
    public string digest { get; set; }
    public long size { get; set; }
}
public class Release {
    public string tag_name { get; set; }
    public string name { get; set; }
    [ScriptIgnore] public bool AllowExperimental;
    [ScriptIgnore] public bool Experimental { get { return prerelease || Regex.IsMatch(name ?? "", @"\b(experimental|alpha|beta|preview|rc)\b", RegexOptions.IgnoreCase); } }
    public string body { get; set; }
    public string published_at { get; set; }
    public bool draft { get; set; }
    public bool prerelease { get; set; }
    public Asset[] assets { get; set; }
    public Version Number { get { return Updater.ParseVersion(tag_name); } }
    public string DisplayVersion { get { return (tag_name ?? "").StartsWith("v", StringComparison.Ordinal) ? tag_name.Substring(1) : tag_name; } }
    public string InstallationIssue {
        get {
            try { Validate(); return null; }
            catch (InvalidDataException ex) { return ex.Message; }
        }
    }
    public object Summary() {
        string issue = InstallationIssue;
        return new {version=DisplayVersion,date=published_at,notes=String.IsNullOrWhiteSpace(body)?"No release notes available.":body,
            experimental=Experimental,installable=issue==null,installationIssue=issue};
    }
    public Asset Package {
        get {
            var matches = (assets ?? new Asset[0]).Where(a => a != null && a.name == "TpF2Multiplayer.msi").ToArray();
            if (matches.Length != 1) throw new InvalidDataException("The release must contain exactly one multiplayer MSI installer.");
            return matches[0];
        }
    }
    public void Validate() {
        if (draft || (Experimental && !AllowExperimental) || Number == null) throw new InvalidDataException("This is not a supported stable Silver release.");
        var asset = Package;
        if (asset.browser_download_url != "https://github.com/" + Updater.Repository + "/releases/download/" + tag_name + "/TpF2Multiplayer.msi" ||
            asset.digest == null || !Regex.IsMatch(asset.digest, @"\Asha256:[0-9a-fA-F]{64}\z") || asset.size <= 0 || asset.size > 536870912)
            throw new InvalidDataException("The download URL, size or SHA-256 checksum is missing or invalid.");
    }
}
public sealed class HttpDownload : WebClient {
    public HttpDownload() { Headers[HttpRequestHeader.UserAgent] = "TPF2-Personal-Launcher/1.0"; Headers[HttpRequestHeader.CacheControl] = "no-cache"; }
    protected override WebRequest GetWebRequest(Uri address) {
        var request = base.GetWebRequest(address);
        request.Timeout = 30000;
        var http = request as HttpWebRequest;
        if (http != null) http.ReadWriteTimeout = 30000;
        return request;
    }
}
public static class Updater {
    // The only release source; the launcher never installs builds from other repositories.
    public const string Repository = "silver2127/tpf2-multiplayer";
    // An elevated helper may run as a different Windows account; it then receives
    // the invoking user's local data folder instead of using its own.
    public static string LocalData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public static string Home { get { return Path.Combine(LocalData, "TPF2-MP", "OfficialLauncher"); } }
    public static Version ParseVersion(string value) {
        Version parsed;
        if (value == null || !Regex.IsMatch(value, @"\Av?[0-9]+\.[0-9]+(?:\.[0-9]+){0,2}\z") ||
            !Version.TryParse(value.TrimStart('v'), out parsed)) throw new InvalidDataException("Invalid version number.");
        return parsed;
    }
    public static bool IsNewer(string installed, string offered) { return ParseVersion(offered) > ParseVersion(installed); }
    public static bool SameVersion(string left, string right) {
        var a=ParseVersion(left); var b=ParseVersion(right);
        return a.Major==b.Major && a.Minor==b.Minor && Math.Max(0,a.Build)==Math.Max(0,b.Build) && Math.Max(0,a.Revision)==Math.Max(0,b.Revision);
    }
    public static string RegistryValue(string name) {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            using (var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
            using (var key = root.OpenSubKey(@"SOFTWARE\silver2127\TpF2 Multiplayer"))
                if (key != null && key.GetValue(name) is string) return (string)key.GetValue(name);
        return null;
    }
    public static bool GameRunning() {
        var processes = Process.GetProcessesByName("TransportFever2");
        try { return processes.Length > 0; } finally { foreach (var process in processes) process.Dispose(); }
    }
    public static void RequireClosed() { if (GameRunning()) throw new InvalidOperationException("Close all Transport Fever 2 windows first."); }
    public static string GameFolder() {
        string config=Path.Combine(Home,"game-folder.txt");
        var candidates=new System.Collections.Generic.List<string>();
        if(File.Exists(config))candidates.Add(File.ReadAllText(config).Trim());
        candidates.Add(RegistryValue("InstallFolder"));
        foreach(var view in new[]{RegistryView.Registry64,RegistryView.Registry32})
            using(var root=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,view))
            using(var key=root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 1066780"))
                if(key!=null)candidates.Add(key.GetValue("InstallLocation") as string);
        foreach(string folder in candidates)
            if(!String.IsNullOrWhiteSpace(folder)&&File.Exists(Path.Combine(folder,"TransportFever2.exe")))return Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
        throw new InvalidOperationException("Game folder not found. Use Select game folder.");
    }
    public static async Task<Release> Fetch(bool experimental = false) {
        var candidates = new System.Collections.Generic.List<Release>();
        for (int page=1; page<=10; page++) {
            var releases = await FetchReleasePage(page, 100);
            candidates.AddRange(releases);
            if (releases.Length < 100) break;
            if (page == 10) throw new InvalidDataException("Release list is too large to determine the latest version safely.");
        }
        return SelectRelease(candidates, experimental);
    }
    internal static Release SelectRelease(System.Collections.Generic.IEnumerable<Release> releases, bool experimental) {
        var selected = releases.Where(r => r != null && !r.draft && r.Experimental == experimental)
            .OrderByDescending(r => PublishedAt(r)).FirstOrDefault();
        if (selected != null) selected.AllowExperimental=experimental;
        return selected;
    }
    static DateTimeOffset PublishedAt(Release release) {
        DateTimeOffset date;
        return DateTimeOffset.TryParse(release.published_at, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out date) ? date : DateTimeOffset.MinValue;
    }
    static async Task<Release[]> FetchReleasePage(int page, int count) {
        using (var client = new HttpDownload()) {
            var job=client.DownloadStringTaskAsync("https://api.github.com/repos/" + Repository + "/releases?per_page="+count+"&page="+page);
            if(await Task.WhenAny(job,Task.Delay(45000))!=job){client.CancelAsync();throw new TimeoutException("GitHub did not respond. Try again later.");}
            var releases=new JavaScriptSerializer{MaxJsonLength=8388608}.Deserialize<Release[]>(await job);
            if(releases==null)throw new InvalidDataException("Empty response from GitHub.");
            return releases;
        }
    }
    public static async Task<Release> FetchVersion(string version, bool allowExperimental = false) {
        ParseVersion(version);
        foreach (string tag in new[] {"v" + version, version}) {
            using (var client = new HttpDownload()) {
                try {
                    var job = client.DownloadStringTaskAsync("https://api.github.com/repos/" + Repository + "/releases/tags/" + tag);
                    if (await Task.WhenAny(job, Task.Delay(45000)) != job) { client.CancelAsync(); throw new TimeoutException("GitHub did not respond. Try again later."); }
                    var release = new JavaScriptSerializer().Deserialize<Release>(await job);
                    if (release == null || release.Number.ToString() != version) throw new InvalidDataException("The requested release does not match.");
                    release.AllowExperimental=allowExperimental; release.Validate(); return release;
                } catch (WebException ex) {
                    var response = ex.Response as HttpWebResponse;
                    if (response == null || response.StatusCode != HttpStatusCode.NotFound) throw;
                }
            }
        }
        throw new InvalidDataException("This release is no longer available.");
    }
    public static async Task<object> History(int page, bool experimental = false) {
        if (page < 1 || page > 10000) throw new InvalidDataException("Invalid history page.");
        var releases=await FetchReleasePage(page,20);
        var entries = releases.Where(r => r != null && !r.draft && r.Experimental == experimental)
            .Select(r => { r.AllowExperimental=experimental; return r.Summary(); }).ToArray();
        return new {entries=entries,hasMore=releases.Length==20};
    }
    public static void Verify(Release release, string file) {
        release.Validate();
        if (new FileInfo(file).Length != release.Package.size) throw new InvalidDataException("The download is incomplete.");
        using (var input = File.OpenRead(file)) using (var sha = SHA256.Create()) {
            string actual = BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "");
            if (!String.Equals(actual, release.Package.digest.Substring(7), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("SHA-256 verification failed. Installation stopped.");
        }
    }
    public static async Task<string> Download(Release release, Action<int> progress) {
        release.Validate(); Directory.CreateDirectory(Path.Combine(Home, "Downloads"));
        string file = Path.Combine(Home, "Downloads", "TpF2Multiplayer-" + release.Number + ".msi");
        if (File.Exists(file)) { try { Verify(release, file); return file; } catch (InvalidDataException) { } }
        string partial = file + "." + Guid.NewGuid().ToString("N") + ".part";
        try {
            using (var client = new HttpDownload()) {
                int reported = -1;
                client.DownloadProgressChanged += (s, e) => { if (e.ProgressPercentage != reported) { reported = e.ProgressPercentage; progress(reported); } };
                var job = client.DownloadFileTaskAsync(release.Package.browser_download_url, partial);
                if (await Task.WhenAny(job, Task.Delay(600000)) != job) { client.CancelAsync(); throw new TimeoutException("Download timed out."); }
                await job;
            }
            await Task.Run(() => Verify(release, partial));
            if (File.Exists(file)) File.Delete(file);
            File.Move(partial, file); return file;
        } finally { try { if (File.Exists(partial)) File.Delete(partial); } catch { } }
    }
    public static string Backup(string game, string version) {
        RequireClosed();
        return BackupFiles(game, version);
    }
    internal static string BackupFiles(string game, string version) {
        string folder = Path.Combine(Home, "Backups", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(folder);
        using (var zip = ZipFile.Open(Path.Combine(folder, "modstand.zip"), ZipArchiveMode.Create)) {
            foreach (string name in new[] { "alut.dll", "alut_real.dll", "tpf2_pluginhost.dll", "tpf2_bridge_mp.dll", "tpf2_slice.dll", "tpf2_menu.dll", "tpf2_slice.cfg", "tpf2mp.cfg", "netpunch/netpunch.exe" }) {
                string source = Path.Combine(game, name);
                if (File.Exists(source)) zip.CreateEntryFromFile(source, name, CompressionLevel.Optimal);
            }
            string mod = Path.Combine(game, "mods", "mp_lockstep_1");
            if (Directory.Exists(mod)) foreach (string file in Directory.GetFiles(mod, "*", SearchOption.AllDirectories))
                zip.CreateEntryFromFile(file, file.Substring(game.Length + 1).Replace('\\', '/'), CompressionLevel.Optimal);
        }
        File.WriteAllText(Path.Combine(folder, "README.txt"), "Mod files saved before the Silver update. Installed: " + version + "\r\nGame: " + game + "\r\nIncludes local Lua changes. Not a complete MSI rollback. To restore, install the matching Silver version, close the game and copy these files back. Saved games are not modified.\r\n");
        return folder;
    }
}
