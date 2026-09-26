using System;
using System.IO;
using System.Linq;
public static class UnitTests {
    static int count;
    static void Check(bool value,string label){if(!value)throw new Exception(label);count++;Console.WriteLine("PASS "+label);}
    static void Reject(Action action,string label){try{action();}catch(InvalidDataException){Check(true,label);return;}throw new Exception("Accepted "+label);}
    static void StockAudioGuards(){
        string game=Path.Combine(Path.GetTempPath(),"tpf2-audio-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(game);
        foreach(string scenario in new[]{"missing","mod loader","modified backup"}) {
            if(scenario=="mod loader")File.WriteAllText(Path.Combine(game,"alut.dll"),"mod loader fixture");
            if(scenario=="modified backup")File.WriteAllText(Path.Combine(game,"alut_real.dll"),"modified backup fixture");
            bool rejected=false;try{LauncherSetup.PreserveStockAudio(game);}catch(InvalidOperationException ex){rejected=ex.Message.Contains("Steam");}
            Check(rejected,"stock audio preflight rejects "+scenario+" with repair guidance");
        }
        Check(File.ReadAllText(Path.Combine(game,"alut.dll"))=="mod loader fixture"&&File.ReadAllText(Path.Combine(game,"alut_real.dll"))=="modified backup fixture","stock audio preflight never overwrites unknown files");
    }
    static void Removal(){
        string game=Path.Combine(Path.GetTempPath(),"tpf2-removal-test-"+Guid.NewGuid().ToString("N"));
        foreach(string name in new[]{"alut_real.dll","alut.dll","tpf2_pluginhost.dll","tpf2mp_version.txt","mods/mp_lockstep_1/res/scripts/mp/net.lua","netpunch/netpunch.exe","netpunch/_internal/runtime.dll","plugins/tpf2_bigmap.dll",
            "save.sav","plugins/unrelated.dll","mods/other_mod/mod.lua","netpunch/user-data.txt"}){
            string file=Path.Combine(game,name.Replace('/','\\'));Directory.CreateDirectory(Path.GetDirectoryName(file));File.WriteAllText(file,name);
        }
        File.WriteAllText(Path.Combine(game,"alut_real.dll"),"stock audio fixture");
        string stock=ProfileStore.Hash(Path.Combine(game,"alut_real.dll"));
        LauncherSetup.RemoveLeftovers(game,stock);
        Check(File.ReadAllText(Path.Combine(game,"alut.dll"))=="stock audio fixture"&&!File.Exists(Path.Combine(game,"alut_real.dll")),"removal restores the stock audio DLL");
        Check(!LauncherSetup.HasLeftovers(game)&&!Directory.Exists(Path.Combine(game,"mods","mp_lockstep_1"))&&!Directory.Exists(Path.Combine(game,"netpunch","_internal")),"removal deletes every launcher-managed file");
        Check(new[]{"save.sav","plugins/unrelated.dll","mods/other_mod/mod.lua","netpunch/user-data.txt"}.All(name=>File.ReadAllText(Path.Combine(game,name.Replace('/','\\')))==name),"removal keeps saves, other mods, plugins and user data");
        File.WriteAllText(Path.Combine(game,"alut.dll"),"mod loader");File.WriteAllText(Path.Combine(game,"tpf2_menu.dll"),"leftover");
        bool refused=false;try{LauncherSetup.RemoveLeftovers(game,stock);}catch(InvalidOperationException ex){refused=ex.Message.Contains("Steam");}
        Check(refused&&!File.Exists(Path.Combine(game,"tpf2_menu.dll"))&&File.ReadAllText(Path.Combine(game,"alut.dll"))=="mod loader","removal without stock backup points to Steam and never guesses the audio DLL");
    }
    static void RuntimeProfiles(){
        string root=Path.Combine(Path.GetTempPath(),"tpf2-runtime-test-"+Guid.NewGuid().ToString("N"));
        string game=Path.Combine(root,"game");Directory.CreateDirectory(game);
        foreach(string name in new[]{"alut.dll","tpf2_pluginhost.dll","tpf2_bridge_mp.dll","tpf2_slice.dll","tpf2_menu.dll","netpunch/netpunch.exe","mods/mp_lockstep_1/mod.lua","mods/mp_lockstep_1/res/config/game_script/lockstep.lua","mods/mp_lockstep_1/res/scripts/mp/net.lua"}){
            string file=ProfileStore.SafePath(game,name);Directory.CreateDirectory(Path.GetDirectoryName(file));File.WriteAllText(file,name);
        }
        string sentinel=Path.Combine(game,"netpunch","user-data.txt");File.WriteAllText(sentinel,"preserve");
        var store=new ProfileStore(Path.Combine(root,"store"),false);store.Initialize(game,"0.6.2.10");var single=store.Active;
        File.WriteAllText(Path.Combine(game,"TransportFever2.exe"),"game fixture");
        File.WriteAllText(Path.Combine(game,"alut_real.dll"),"stock audio fixture");
        Check(store.Installed(game).Id==single.Id,"complete installation detected from game files");
        foreach(var entry in single.Files) {
            string path=ProfileStore.SafePath(game,entry.Path),contents=File.ReadAllText(path);
            File.Delete(path);
            Check(new ProfileStore(store.Root,false).Installed(game)==null,"stale profile rejected after removal of "+entry.Path);
            File.WriteAllText(path,contents);
        }
        File.WriteAllText(Path.Combine(game,"alut.dll"),"stock audio fixture");
        Check(store.Installed(game)==null&&!ProfileStore.HasModFiles(game),"Steam-restored stock loader is not multiplayer");
        File.WriteAllText(Path.Combine(game,"alut.dll"),"different mod loader");
        Check(store.Installed(game)==null,"replaced loader cannot claim cached version");
        File.WriteAllText(Path.Combine(game,"alut.dll"),"alut.dll");
        Check(store.Installed(null)==null&&store.Installed(Path.Combine(root,"other-game"))==null,"missing or different game folder is not installed");
        File.Delete(Path.Combine(game,"TransportFever2.exe"));
        Check(store.Installed(game)==null,"uninstalled game is not reported as installed");
        File.WriteAllText(Path.Combine(game,"TransportFever2.exe"),"game fixture");
        File.AppendAllText(ProfileStore.SafePath(game,"mods/mp_lockstep_1/mod.lua"),"local edit");
        Check(store.Installed(game)!=null,"local Lua edits do not hide installed mod");
        File.WriteAllText(ProfileStore.SafePath(game,"mods/mp_lockstep_1/mod.lua"),"mods/mp_lockstep_1/mod.lua");
        store.Initialize(game,"0.7",true);
        Check(store.Active.Version=="0.7"&&store.Installed(game)!=null,"base reinstall replaces stale active state");
        store.Verify(single);Check(true,"base reinstall preserves previous backup");
        string bigmapDll=ProfileStore.SafePath(game,"plugins/tpf2_bigmap.dll");
        string bigmapCfg=ProfileStore.SafePath(game,"plugins/tpf2_bigmap.cfg");
        Directory.CreateDirectory(Path.GetDirectoryName(bigmapDll));
        File.WriteAllText(bigmapDll,"big maps DLL");File.WriteAllText(bigmapCfg,"big maps settings");
        string otherPlugin=Path.Combine(game,"plugins","unrelated.cfg");File.WriteAllText(otherPlugin,"preserve");
        var bigmap=store.Capture(game,"official","0.7","Big Maps fixture","");
        Check(bigmap.Files.Any(f=>f.Path=="plugins/tpf2_bigmap.dll")&&bigmap.Files.Any(f=>f.Path=="plugins/tpf2_bigmap.cfg"),"Big Maps DLL and config are backed up and hashed");
        store.Activate(bigmap);store.Activate(single);
        Check(!File.Exists(bigmapDll)&&!File.Exists(bigmapCfg),"older release removes both Big Maps components");
        store.Activate(bigmap);
        bool bigmapFailed=false;try{store.Activate(single,n=>{if(!File.Exists(bigmapCfg))throw new IOException("fixture Big Maps removal fault");});}catch(IOException){bigmapFailed=true;}
        Check(bigmapFailed&&store.Matches(bigmap,game)&&File.ReadAllText(bigmapCfg)=="big maps settings"&&File.Exists(bigmapDll),"rollback restores Big Maps DLL and configuration");
        Check(File.ReadAllText(otherPlugin)=="preserve"&&!ProfileStore.Managed("plugins/unrelated.cfg"),"unrelated plugin files stay protected");
        store.Activate(single);
        string relative="netpunch/_internal/api-ms-win-core-console-l1-1-0.dll";
        string runtime=ProfileStore.SafePath(game,relative);Directory.CreateDirectory(Path.GetDirectoryName(runtime));File.WriteAllText(runtime,"runtime fixture");
        string nested=ProfileStore.SafePath(game,"netpunch/_internal/package/data.bin");Directory.CreateDirectory(Path.GetDirectoryName(nested));File.WriteAllText(nested,"nested data");
        var bundled=store.Capture(game,"official","0.6.1.12","fixture","");
        Check(bundled.Files.Any(f=>f.Path==relative)&&bundled.Files.Any(f=>f.Path.EndsWith("package/data.bin")),"runtime subtree captured");
        store.Activate(bundled);store.Activate(single);
        Check(!File.Exists(runtime)&&!File.Exists(nested)&&store.Matches(single,game),"single-file profile removes bundled runtime");
        store.Activate(bundled);Check(store.Matches(bundled,game),"bundled runtime restored and hashed");
        bool failed=false;try{store.Activate(single,n=>{if(!File.Exists(runtime))throw new IOException("fixture removal fault");});}catch(IOException){failed=true;}
        Check(failed&&store.Matches(bundled,game)&&File.Exists(runtime)&&File.Exists(nested)&&!store.RecoveryPending,"rollback restores removed runtime");
        Check(File.ReadAllText(sentinel)=="preserve","unmanaged netpunch data preserved");
        File.AppendAllText(runtime,"modified");Check(!store.Matches(bundled,game),"runtime changes detected");
        Check(store.BackupLimit==5,"default backup retention is five");
        Reject(()=>store.SetBackupLimit(-1),"negative retention rejected");
        Reject(()=>store.SetBackupLimit(2),"unsupported retention rejected");
        var snapshots = new System.Collections.Generic.List<Profile>();
        for (int i=0;i<4;i++) {
            var snapshot=store.Capture(game,"local","0.6.1.12","retention","");
            snapshot.CreatedUtc=DateTime.UtcNow.AddDays(10+i).ToString("o");
            ProfileStore.Write(Path.Combine(store.Root,"Profiles",snapshot.Id,"profile.json"),snapshot);
            snapshots.Add(snapshot);
        }
        string profiles=Path.Combine(store.Root,"Profiles");
        string unknown=Path.Combine(profiles,"unmanaged");Directory.CreateDirectory(unknown);File.WriteAllText(Path.Combine(unknown,"keep.txt"),"keep");
        store.SetBackupLimit(0);int before=Directory.GetDirectories(profiles).Length;store.PruneBackups();
        Check(Directory.GetDirectories(profiles).Length==before,"unlimited preserves backups");
        store.SetBackupLimit(1);
        Check(new ProfileStore(store.Root,false).BackupLimit==1,"backup limit persists");
        string journal=Path.Combine(store.Root,"switch-pending.json");
        ProfileStore.Write(journal,new SwitchJournal{Previous=store.State,Restore=snapshots[0].Id});
        store.PruneBackups();Check(Directory.GetDirectories(profiles).Length==before,"pending recovery prevents all cleanup");File.Delete(journal);
        store.State.Official=snapshots[0].Id;store.Save();
        store.PruneBackups();
        Check(store.State.Official==null,"cached release reference does not retain old backups forever");
        Check(Directory.GetDirectories(profiles).Length==3,"one historical backup plus active profile and unmanaged folder");
        Check(Directory.Exists(Path.Combine(profiles,snapshots[3].Id))&&!Directory.Exists(Path.Combine(profiles,snapshots[0].Id)),"newest backup kept and oldest removed");
        store.Verify(store.Active);Check(true,"active profile survives cleanup");
        if(store.State.Official!=null)store.Verify(store.Load(store.State.Official));
        Check(File.Exists(Path.Combine(unknown,"keep.txt")),"unknown folders remain untouched");
        Check(store.Matches(bundled,game)==false&&File.ReadAllText(sentinel)=="preserve","cleanup does not modify game files");
        string downloads=Path.Combine(store.Root,"Downloads");Directory.CreateDirectory(downloads);
        File.WriteAllText(Path.Combine(downloads,"TpF2Multiplayer-0.6.1.14.msi"),"cached");
        File.WriteAllText(Path.Combine(downloads,"TpF2Multiplayer-0.6.1.18.msi"),"cached");
        File.WriteAllText(Path.Combine(downloads,"unrelated.msi"),"keep");
        LauncherSetup.CleanDownloads(store.Root);
        Check(Directory.GetFiles(downloads).Length==1&&File.Exists(Path.Combine(downloads,"unrelated.msi")),"completed mod installers removed, unknown files preserved");
        string stage=Path.Combine(Path.GetTempPath(),"tpf2-"+Guid.NewGuid().ToString("N").Substring(0,12));Directory.CreateDirectory(stage);
        Directory.CreateDirectory(Path.Combine(stage,"nested"));File.WriteAllText(Path.Combine(stage,"nested","fixture"),"temporary");
        LauncherSetup.CleanStage(stage);Check(!Directory.Exists(stage),"owned extraction directory removed");
        LauncherSetup.CleanStage(root);Check(Directory.Exists(root),"cleanup rejects unrelated temp directory");
        var holder=new System.Threading.Thread(()=>new System.Threading.Mutex(false,@"Local\TPF2ReleaseProfileMutation").WaitOne());
        holder.Start();holder.Join();
        var current=store.Active;store.Activate(current);
        Check(store.Matches(current,game),"installation proceeds after a crashed helper abandoned the lock");
        Console.WriteLine("Runtime fixtures: "+root);
    }
    public static void Main(){
        StockAudioGuards();
        Check(Updater.IsNewer("0.6.1","v0.6.1.1"),"four-part hotfix");
        Check(!Updater.IsNewer("0.6.1.1","v0.6.1"),"no implicit downgrade");
        Check(Updater.IsNewer("0.6.1.9","v0.6.1.10"),"numeric comparison");
        Check(Updater.ParseVersion("v0.7").ToString()=="0.7","two-part release version preserved");
        Check(Updater.IsNewer("0.6.1.19","v0.7"),"two-part release is newer than four-part release");
        Check(Updater.SameVersion("0.7","0.7.0") && Updater.SameVersion("0.7","0.7.0.0"),"MSI zero padding matches release tag");
        Check(!Updater.SameVersion("0.7","0.7.0.1") && !Updater.SameVersion("0.7","0.8.0"),"different MSI versions remain rejected");
        var old=new Release{tag_name="v9.0.0",published_at="2026-09-21T10:00:00Z"};
        var latest=new Release{tag_name="release-autumn",published_at="2026-09-22T10:00:00Z"};
        var draft=new Release{tag_name="v10.0",published_at="2026-09-23T10:00:00Z",draft=true};
        var preview=new Release{tag_name="v11.0",published_at="2026-09-23T10:00:00Z",prerelease=true};
        Check(Updater.SelectRelease(new[]{old,draft,latest,preview},false)==latest,"latest published stable release shown regardless of tag or package");
        Check(Updater.SelectRelease(new[]{old,latest,preview},true)==preview,"experimental selection stays separate");
        var summary=new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(latest.Summary());
        Check(summary.Contains("release-autumn") && summary.Contains("\"installable\":false"),"unsupported release remains visible with installation reason");
        var missing=new Release{tag_name="v0.7",published_at="2026-09-24T10:00:00Z"};
        Check(Updater.SelectRelease(new[]{old,missing},false)==missing && missing.InstallationIssue!=null,"missing MSI does not hide newest release");
        // 0.7.0.6 on: the mod release carries the launchers, the install files are in the
        // packages repository, and the mod repository keeps a copy tagged without the "v"
        // for launchers before 1.3.0, published after it; launcher updates are launcher-v*.
        var twoLaunchers=new Release{tag_name="v0.7.0.6",published_at="2026-09-27T10:00:00Z"};
        var legacyCopy=new Release{tag_name="0.7.0.6",published_at="2026-09-27T10:01:00Z"};
        var launcherUpdate=new Release{tag_name="launcher-v1.3.0",published_at="2026-09-28T10:00:00Z"};
        Check(Updater.SelectRelease(new[]{old,twoLaunchers,legacyCopy,launcherUpdate},false)==twoLaunchers,"the legacy install-file copy and launcher updates are not mod versions");
        Check(Updater.SelectRelease(new[]{old,legacyCopy},false)==legacyCopy,"a tag without v and without a v twin is still a release");
        var msiAt=new Func<string,Asset>(repo=>new Asset{name="TpF2Multiplayer.msi",size=1,digest="sha256:"+new string('b',64),browser_download_url="https://github.com/"+repo+"/releases/download/v0.7.0.6/TpF2Multiplayer.msi"});
        Reject(()=>twoLaunchers.Validate(),"a two-launcher release alone has no MSI");
        Updater.UsePackages(twoLaunchers,new Release{tag_name="v0.7.0.5",assets=new[]{msiAt(Updater.PackagesRepository)}});
        Check(twoLaunchers.AssetsRepository==Updater.Repository,"packages of another tag are not taken");
        Updater.UsePackages(twoLaunchers,new Release{tag_name="v0.7.0.6",assets=new[]{msiAt(Updater.PackagesRepository)}});
        twoLaunchers.Validate();Check(twoLaunchers.AssetsRepository==Updater.PackagesRepository,"install files from the packages release with the same tag");
        twoLaunchers.assets=new[]{msiAt(Updater.Repository)};
        Reject(()=>twoLaunchers.Validate(),"a packages release must link its own files");
        twoLaunchers.assets=new[]{msiAt("tearded/tpf2-multiplayer-packages")};
        Reject(()=>twoLaunchers.Validate(),"packages from another account rejected");
        foreach(string version in new[]{"v0.6.1.1-beta","1","1.0.0.0.1","0.6.1.999999999999999999","1.0.0\n"})Reject(()=>Updater.ParseVersion(version),"invalid version");
        foreach(string path in new[]{"../outside.dll","C:/Windows/file.dll","plugins/other.dll","alut_real.dll","mods/mp_lockstep_1/../other.lua","mods/mp_lockstep_1/test.lua:stream"})
            Reject(()=>ProfileStore.SafePath(Path.GetTempPath(),path),"path restriction "+path);
        Check(ProfileStore.Managed("mods/mp_lockstep_1/res/scripts/mp/net.lua"),"managed Lua");
        Check(ProfileStore.Managed("plugins/tpf2_workshop_register.dll"),"managed workshop plugin");
        foreach(string path in new[]{"netpunch/_internal/../outside.dll","netpunch/_internal/file.dll:stream","netpunch/_internal-other/file.dll","netpunch/user-data.txt","netpunch/_internal/pkg/../../outside.dll"})
            Reject(()=>ProfileStore.SafePath(Path.GetTempPath(),path),"runtime path restriction "+path);
        var saved=new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<ProfileState>("{\"GameFolder\":\"game\",\"Active\":\"a\",\"Official\":null,\"Fork\":null}");
        Check(saved.Active=="a"&&saved.GameFolder=="game","state files from launcher 1.0.x with a Fork entry still load");
        Reject(()=>LauncherSetup.RequireSilver(new Profile{Channel="local"}),"manual local activation blocked");
        RuntimeProfiles();
        Removal();
        string data=Path.Combine(Path.GetTempPath(),"tpf2-data-test-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(data,"TPF2-MP","OfficialLauncher"));
        Reject(()=>LauncherSetup.ElevatedDataFolder(data),"elevated helper rejects a data folder without launcher state");
        File.WriteAllText(Path.Combine(data,"TPF2-MP","OfficialLauncher","profiles.json"),"{}");
        Check(LauncherSetup.ElevatedDataFolder(data)==data,"elevated helper uses the invoking user's data folder");
        foreach(string value in new[]{null,"","relative",@"\\server\share",Path.Combine(data,"..",Path.GetFileName(data))})
            Reject(()=>LauncherSetup.ElevatedDataFolder(value),"elevated data folder restriction "+value);
        var asset=new Asset{name="TpF2Multiplayer.msi",size=1,digest="sha256:"+new string('a',64),browser_download_url="https://github.com/silver2127/tpf2-multiplayer/releases/download/v0.6.1.1/TpF2Multiplayer.msi"};
        var release=new Release{tag_name="v0.6.1.1",assets=new[]{asset}};release.Validate();Check(true,"valid fixed source");
        asset.browser_download_url="https://github.com/tearded/tpf2-multiplayer/releases/download/v0.6.1.1/TpF2Multiplayer.msi";
        Reject(()=>release.Validate(),"package from another repository rejected");
        asset.browser_download_url="https://github.com/silver2127/tpf2-multiplayer/releases/download/v0.6.1.1/TpF2Multiplayer.msi";
        release.prerelease=true;Reject(()=>release.Validate(),"prerelease blocked by default");
        release.AllowExperimental=true;release.Validate();Check(true,"explicit experimental selection allows verified prerelease");
        release.AllowExperimental=false;release.prerelease=false;
        release.name="0.6.1.1 (experimental)";
        Check(release.Experimental,"experimental title recognized without GitHub prerelease flag");
        Reject(()=>release.Validate(),"title-marked experimental release blocked in stable mode");
        Check(Updater.SelectRelease(new[]{release},false)==null,"stable feed excludes title-marked experimental release");
        Check(Updater.SelectRelease(new[]{release},true)==release,"experimental feed includes title-marked release");
        release.AllowExperimental=false;release.name="Stable with experimental fixes";
        Check(release.Experimental,"ambiguous experimental title is conservatively classified");
        release.name="0.6.1.1";release.body="Fixes a bug reported in experimental builds.";
        Check(!release.Experimental,"historical experimental mention in body does not misclassify stable release");
        Reject(()=>Updater.FetchVersion("0.6.1-beta").GetAwaiter().GetResult(),"non-numeric tag rejected before network access");
        release.draft=true;Reject(()=>release.Validate(),"draft blocked");release.draft=false;
        asset.browser_download_url="https://example.com/TpF2Multiplayer.msi";Reject(()=>release.Validate(),"foreign source blocked");
        Console.WriteLine("PASS total="+count);
    }
}
