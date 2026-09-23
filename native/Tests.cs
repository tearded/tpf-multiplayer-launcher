using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Drawing;
using System.Windows.Forms;
public static class Tests {
    static int count;
    static string output=Path.GetDirectoryName(typeof(Tests).Assembly.Location);
    static void Check(bool ok,string name){if(!ok)throw new Exception(name);count++;Console.WriteLine("PASS "+name);}
    static void Reject(Action action,string name){try{action();}catch(InvalidDataException){Check(true,name);return;}throw new Exception("Accepted: "+name);}
    static void Pump(Func<bool> done){var deadline=DateTime.UtcNow.AddSeconds(55);while(!done()){if(DateTime.UtcNow>deadline)throw new TimeoutException("UI test timeout");Application.DoEvents();System.Threading.Thread.Sleep(20);}Application.DoEvents();}
    static void Screenshot(Form form,string name){using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(0,0,form.Width,form.Height));bitmap.Save(Path.Combine(output,name));}}
    [STAThread] public static void Main(){
        ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;
        Check(Updater.IsNewer("0.4.9","v0.4.19"),"numerischer Vergleich");
        Check(!Updater.IsNewer("0.4.19","v0.4.19"),"gleiche Version bleibt");
        Check(!Updater.IsNewer("0.5.0","v0.4.19"),"kein automatisches Downgrade");
        Reject(()=>Updater.ParseVersion("v0.4.20-beta"),"keine Vorabversion");
        Check(Updater.ParseVersion("v0.6.1.1").ToString()=="0.6.1.1","vierstellige Releaseversion bleibt erhalten");
        Check(Updater.IsNewer("0.6.1","v0.6.1.1"),"Hotfix neuer als dreistellige Version");
        Check(Updater.IsNewer("0.6.1.9","v0.6.1.10"),"Hotfix numerisch vergleichen");
        Check(!Updater.IsNewer("0.6.1.1","v0.6.1.1"),"gleicher Hotfix bleibt");
        Check(!Updater.IsNewer("0.6.1.1","v0.6.1"),"kein Hotfix-Downgrade");
        foreach(string badVersion in new[]{"0", "0.6.1.1.1", "v0.6.1.1-beta", "0.6.1.1\n", "0.6.1.-1", "0.6.1.999999999999999999"})
            Reject(()=>Updater.ParseVersion(badVersion),"ungueltige Releaseversion: "+badVersion.Trim());
        var release=Updater.Fetch().GetAwaiter().GetResult();Console.WriteLine("LIVE official "+release.tag_name);
        Reject(()=>Updater.Fetch("fork").GetAwaiter().GetResult(),"legacy fork downloads blocked");
        var asset=release.Package;string digest=asset.digest,url=asset.browser_download_url;
        release.draft=true;Reject(()=>release.Validate(),"kein Entwurf");release.draft=false;
        release.prerelease=true;Reject(()=>release.Validate(),"keine prerelease-Metadaten");release.prerelease=false;
        asset.digest=null;Reject(()=>release.Validate(),"fehlender Hash");asset.digest=digest;
        asset.browser_download_url="https://example.com/TpF2Multiplayer.msi";Reject(()=>release.Validate(),"fremde Downloadquelle");asset.browser_download_url=url;
        release.Repository="tearded/tpf2-multiplayer";Reject(()=>release.Validate(),"offizielles Asset nicht als Fork-Download akzeptiert");
        asset.browser_download_url="https://github.com/tearded/tpf2-multiplayer/releases/download/"+release.tag_name+"/TpF2Multiplayer.msi";Reject(()=>release.Validate(),"community release rejected even with matching metadata");
        release.Repository="silver2127/tpf2-multiplayer";asset.browser_download_url=url;
        string package=Updater.Download(release,p=>{}).GetAwaiter().GetResult();Updater.Verify(release,package);Check(true,"echter Release-Download Größe und SHA-256");
        asset.digest="sha256:"+new String('0',64);Reject(()=>Updater.Verify(release,package),"manipulierter Hash");asset.digest=digest;
        asset.size++;Reject(()=>Updater.Verify(release,package),"falsche Dateigröße");asset.size--;
        string root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"TPF2-MP","LauncherTests",Guid.NewGuid().ToString("N").Substring(0,8));Directory.CreateDirectory(root);
        string game=Path.Combine(root,"game");Directory.CreateDirectory(game);
        foreach(string relative in ProfileStore.Inventory(Updater.GameFolder())){
            string target=ProfileStore.SafePath(game,relative);Directory.CreateDirectory(Path.GetDirectoryName(target));File.Copy(ProfileStore.SafePath(Updater.GameFolder(),relative),target);
        }
        File.WriteAllText(Path.Combine(game,"alut_real.dll"),"stock audio sentinel");File.WriteAllText(Path.Combine(game,"save.sav"),"save sentinel");
        Directory.CreateDirectory(Path.Combine(game,"plugins"));File.WriteAllText(Path.Combine(game,"plugins","unrelated.dll"),"other plugin sentinel");
        Directory.CreateDirectory(Path.Combine(game,"mods","other_mod"));File.WriteAllText(Path.Combine(game,"mods","other_mod","mod.lua"),"other mod sentinel");
        var store=new ProfileStore(Path.Combine(root,"store"),false);store.Initialize(game,"0.4.19");
        var initial=store.Active;Check(initial.Channel=="local"&&store.State.Fork==null,"lokale Entwicklung ist kein Fork-Release");
        // Legacy profile fixture for migration/rollback, never downloaded or offered.
        foreach(string relative in new[]{"tpf2mp_version.txt","plugins/tpf2_workshop_register.dll"}) {
            string file=ProfileStore.SafePath(game,relative);if(File.Exists(file))File.Delete(file);
        }
        var publishedFork=store.Capture(game,"fork","0.4.30","Legacy backup fixture","");
        var official=store.PrepareRelease(release,p=>{}).GetAwaiter().GetResult();
        Check(official.Channel=="official"&&official.PackageHash==digest,"offizielles MSI vollständig isoliert entpackt");
        store.Activate(official);Check(store.Matches(official,game),"Wechsel zum offiziellen Release alle Dateihashes");
        string versionMarker=Path.Combine(game,"tpf2mp_version.txt");
        string workshopPlugin=Path.Combine(game,"plugins","tpf2_workshop_register.dll");
        Check(official.Files.Any(f=>f.Path=="tpf2mp_version.txt")&&File.ReadAllText(versionMarker).Trim()==release.Number.ToString(),"offizielle Versionsdatei vollständig geprüft und installiert");
        Check(official.Files.Any(f=>f.Path=="plugins/tpf2_workshop_register.dll")&&File.Exists(workshopPlugin),"offizielles Workshop-Plugin vollständig geprüft und installiert");
        bool markerFailed=false;try{store.Activate(publishedFork,n=>{if(!File.Exists(versionMarker))throw new IOException("Injected failure after version marker removal");});}catch(IOException){markerFailed=true;}
        Check(markerFailed&&store.Matches(official,game)&&File.ReadAllText(versionMarker).Trim()==release.Number.ToString()&&File.Exists(workshopPlugin),"Rollback stellt entfernte Versionsdatei und Workshop-Plugin vollständig wieder her");
        store.Activate(publishedFork);
        Check(!File.Exists(versionMarker)&&!File.Exists(workshopPlugin)&&store.Matches(publishedFork,game),"Rückwechsel zum älteren Fork entfernt Versionsdatei und Workshop-Plugin");
        store.Activate(official);
        Check(ProfileStore.Hash(Path.Combine(game,"plugins","tpf2_previews.dll"))==official.Files.Single(f=>f.Path=="plugins/tpf2_previews.dll").Sha256,"offizielle Vorschau-DLL ersetzt Fork-Version");
        Check(!File.Exists(Path.Combine(game,"mods","mp_lockstep_1","res","scripts","mp","navigation.lua")),"Fork-Zusatzmodul beim offiziellen Stand entfernt");
        store.Activate(initial);Check(store.Matches(initial,game),"vollständiger Rückwechsel inklusive Vorschau-Integration");
        var futureFork=store.Capture(game,"fork","0.4.19","TEST FIXTURE","Synthetic released fork payload; not published.");
        store.Activate(futureFork);store.Activate(official);store.Activate(futureFork);
        Check(store.Active.Channel=="fork"&&store.Matches(futureFork,game),"Kanalwechsel trotz gleicher Versionsnummer");
        bool failed=false;try{store.Activate(official,n=>{if(n==3)throw new IOException("Injected copy failure");});}catch(IOException){failed=true;}
        Check(failed&&store.Matches(futureFork,game)&&!store.RecoveryPending,"Fehler nach drei Dateien rollt vollständig zurück");
        var oldState=ProfileStore.Read<ProfileState>(Path.Combine(store.Root,"profiles.json"));
        ProfileStore.Write(Path.Combine(store.Root,"switch-pending.json"),new SwitchJournal{Previous=oldState,Restore=futureFork.Id});
        File.WriteAllText(Path.Combine(game,"alut.dll"),"interrupted copy");
        var restarted=new ProfileStore(store.Root,false);Check(restarted.RecoveryPending,"Unterbrechung nach Neustart erkannt");restarted.Recover();
        Check(restarted.Matches(futureFork,game)&&!restarted.RecoveryPending,"Wiederherstellung nach simuliertem Prozessabbruch");store=restarted;
        string savedForkId=store.State.Fork;
        string external=Path.Combine(game,"mods","mp_lockstep_1","local-edit.lua");File.WriteAllText(external,"local edited = true");
        store.DetectLocalChanges();Check(store.Active.Channel=="local"&&store.State.Fork==savedForkId,"lokale Änderungen getrennt vom Fork-Release gesichert");
        store.Activate(official);Check(!File.Exists(external),"lokale Zusatzdatei hinterlässt keinen Mischstand");
        foreach(string bad in new[]{"../outside.dll","mods/mp_lockstep_1/../../save.sav","plugins/unrelated.dll","alut_real.dll","mods/mp_lockstep_1/test.lua:stream","C:/Windows/file.dll","unrelated.txt","tpf2mp_version.txt:stream"})Reject(()=>ProfileStore.SafePath(game,bad),"Pfadschutz "+bad);
        string manifest=Path.Combine(store.Root,"Profiles",futureFork.Id,"profile.json");var invalid=ProfileStore.Read<Profile>(manifest);string originalPath=invalid.Files[0].Path;invalid.Files[0].Path="../outside.dll";ProfileStore.Write(manifest,invalid);
        Reject(()=>store.Activate(futureFork),"manipuliertes Profil vor Dateischreibzugriff abgewiesen");
        Check(store.Matches(official,game),"aktives Profil nach ungültigem Ziel unverändert");invalid.Files[0].Path=originalPath;ProfileStore.Write(manifest,invalid);
        string corrupt=Path.Combine(store.Root,"Profiles",futureFork.Id,"files",invalid.Files[0].Path.Replace('/','\\'));File.AppendAllText(corrupt,"corrupt");
        Reject(()=>store.Activate(futureFork),"beschädigte Profildatei vor Wechsel abgewiesen");
        Check(File.ReadAllText(Path.Combine(game,"alut_real.dll"))=="stock audio sentinel"&&File.ReadAllText(Path.Combine(game,"save.sav"))=="save sentinel"&&File.ReadAllText(Path.Combine(game,"plugins","unrelated.dll"))=="other plugin sentinel"&&File.ReadAllText(Path.Combine(game,"mods","other_mod","mod.lua"))=="other mod sentinel","Original-DLL, Spielstände, andere Plugins und Mods unverändert");
        Console.WriteLine("PASS total="+count+" testRoot="+root);
    }
}
