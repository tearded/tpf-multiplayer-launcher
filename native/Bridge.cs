using System;
using System.IO;
using System.Net;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

public static class NativeBridge {
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 2097152 };
    static readonly object OutputLock = new object();
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] static extern int GetCurrentPackageFullName(ref uint length, StringBuilder name);
    static void Write(object value) { lock(OutputLock)Console.WriteLine(Json.Serialize(value)); }
    static void Progress(string text, int percent=0) { Write(new {kind="progress",text=text,percent=percent}); }
    static void Open(string target) { Process.Start(new ProcessStartInfo(target){UseShellExecute=true}); }
    static string Channel(string value) { if(value=="official")return "official";throw new InvalidDataException("Only releases from silver2127 are supported."); }
    static string PublicChannel(string value) { return value=="fork"?"community":value; }
    public static void EnsureNativeContext() {
        uint length=0;
        if(GetCurrentPackageFullName(ref length,null)!=15700)
            throw new InvalidOperationException("Open the launcher from the Windows Start menu. The current app context redirects user data.");
    }
    static void RequireOldLauncherClosed() {
        try { using(var gate=Mutex.OpenExisting(@"Local\TPF2OfficialPersonalLauncher")) {
            bool acquired=false;try{acquired=gate.WaitOne(0);}catch(AbandonedMutexException){acquired=true;}
            if(!acquired)throw new InvalidOperationException("Close the previous launcher first.");
            gate.ReleaseMutex();
        }}catch(WaitHandleCannotBeOpenedException){}
    }
    static object Status(ProfileStore store) {
        string folder=null;try{folder=Updater.GameFolder();}catch(InvalidOperationException){}
        var active=store.Active;
        return new {gameFolder=folder,installed=active==null?null:new {version=active.Version,channel=PublicChannel(active.Channel)},
            running=Updater.GameRunning(),recovery=store.RecoveryPending,initialized=store.State!=null,backupLimit=store.BackupLimit,experimental=store.ExperimentalReleases};
    }
    static void InitializeIfPresent(ProfileStore store) {
        if(store.State!=null||Updater.GameRunning())return;
        string version=Updater.RegistryValue("Version");
        if(version==null)return;
        RequireOldLauncherClosed();
        store.Initialize(Updater.GameFolder(),version);
    }
    static async Task<object> Run(string action,string channel,string expectedVersion) {
        Channel(channel);
        var store=new ProfileStore(Updater.Home,true);
        switch(action) {
            case "release-track": {
                if(expectedVersion!="0" && expectedVersion!="1")throw new InvalidDataException("Invalid release track.");
                store.ExperimentalReleases=expectedVersion=="1";
                return new {experimental=store.ExperimentalReleases};
            }
            case "history": {
                int page;
                if (!Int32.TryParse(expectedVersion, out page)) throw new InvalidDataException("Invalid history page.");
                return await Updater.History(page,store.ExperimentalReleases);
            }
            case "backup-limit": {
                int limit;
                if (!Int32.TryParse(expectedVersion, out limit)) throw new InvalidDataException("Invalid backup limit.");
                store.SetBackupLimit(limit); return new {backupLimit=store.BackupLimit};
            }
            case "status":
                InitializeIfPresent(store);
                return Status(store);
            case "fetch": {
                var release=await Updater.Fetch(Channel(channel),store.ExperimentalReleases);
                if(release==null)return null;
                return release.Summary();
            }
            case "choose-folder": {
                Updater.RequireClosed();RequireOldLauncherClosed();
                using(var dialog=new FolderBrowserDialog{Description="Select the Transport Fever 2 folder containing TransportFever2.exe"}) {
                    if(dialog.ShowDialog()!=DialogResult.OK)return Status(store);
                    string folder=Path.GetFullPath(dialog.SelectedPath);
                    if(!File.Exists(Path.Combine(folder,"TransportFever2.exe")))throw new InvalidDataException("This folder does not contain TransportFever2.exe.");
                    if(store.State!=null&&!String.Equals(folder,store.State.GameFolder,StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Existing backups belong to a different game folder.");
                    Directory.CreateDirectory(Updater.Home);File.WriteAllText(Path.Combine(Updater.Home,"game-folder.txt"),folder);
                }
                InitializeIfPresent(store);return Status(store);
            }
            case "install": {
                Updater.RequireClosed();RequireOldLauncherClosed();InitializeIfPresent(store);
                if(store.RecoveryPending)throw new InvalidOperationException("Restore the interrupted installation first.");
                if(expectedVersion==null)throw new InvalidDataException("Check the available release first.");
                var release=await Updater.FetchVersion(expectedVersion,store.ExperimentalReleases);
                if(release==null||release.Number.ToString()!=expectedVersion)throw new InvalidOperationException("The available release has changed. Check for updates again.");
                if(store.State==null) {
                    Progress("Setting up multiplayer…");
                    await LauncherSetup.InstallBase(release,p=>Progress("Downloading multiplayer…",p));
                    store.Initialize(Updater.GameFolder(),Updater.RegistryValue("Version"));
                }
                Progress("Downloading and verifying release…");
                var target=await store.PrepareRelease(release,p=>Progress("Downloading release…",p));
                Updater.RequireClosed();RequireOldLauncherClosed();
                Progress("Backing up current files and installing release…");
                await LauncherSetup.Activate(store,target);
                try { store.PruneBackups(); LauncherSetup.CleanDownloads(store.Root); }
                catch (Exception) { Progress("Installed successfully. Some older backups could not be removed."); }
                return Status(store);
            }
            case "recover": {
                Updater.RequireClosed();RequireOldLauncherClosed();
                if(!store.RecoveryPending)return Status(store);
                var journal=ProfileStore.Read<SwitchJournal>(Path.Combine(store.Root,"switch-pending.json"));
                await LauncherSetup.Activate(store,store.Load(journal.Restore));return Status(store);
            }
            case "play":
                if(store.Active==null||store.Active.Channel!="official")throw new InvalidOperationException("Install multiplayer before starting the game from this launcher.");
                if(store.RecoveryPending)throw new InvalidOperationException("Restore the interrupted installation first.");
                Updater.RequireClosed();Updater.GameFolder();Open("steam://rungameid/1066780");return new {started=true};
            case "game-folder": Open(Updater.GameFolder());return new {opened=true};
            case "backups":
                string profiles=Path.Combine(store.Root,"Profiles");Directory.CreateDirectory(profiles);Open(profiles);return new {opened=true};
            case "release-link": {
                Uri link;
                if(expectedVersion==null || expectedVersion.Length>2048 || !Uri.TryCreate(expectedVersion,UriKind.Absolute,out link) || link.Scheme!="https" || link.Host!="github.com" || !link.IsDefaultPort || link.UserInfo!="" || !link.AbsolutePath.StartsWith("/silver2127/tpf2-multiplayer/",StringComparison.Ordinal))
                    throw new InvalidDataException("Invalid release link.");
                Open(link.AbsoluteUri);return new {opened=true};
            }
            case "release-page":
                Open("https://github.com/silver2127/tpf2-multiplayer/releases");return new {opened=true};
            default: throw new InvalidDataException("Unknown launcher action.");
        }
    }
    [STAThread] public static int Main(string[] args) {
        Console.OutputEncoding=new UTF8Encoding(false);
        ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;
        try {
            EnsureNativeContext();
            if(LauncherSetup.HandleElevatedAction(args))return 0;
            if(args.Length<2||args.Length>3)throw new InvalidDataException("Invalid launcher arguments.");
            var result=Run(args[0],args[1],args.Length==3?args[2]:null).GetAwaiter().GetResult();
            Write(new {ok=true,data=result});return 0;
        }catch(Exception ex){Write(new {ok=false,error=ex.Message});return 1;}
    }
}
