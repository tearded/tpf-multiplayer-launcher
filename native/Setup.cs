using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

public static class LauncherSetup {
    internal const string StockAudioHash="3DF103AE3D94A6B90C4D2A6D75DCB388CD835F5E3AF9962B22C20D4473CFC035";
    const string UpgradeCode="{80DBF679-F058-410E-9BAD-87731AC96633}";
    public static bool HandleElevatedAction(string[] args) {
        if(args.Length==3&&args[0]=="--remove")return HandleElevatedRemoval(args);
        if(args.Length!=4||args[0]!="--activate")return false;
        Guid profileId,requestId;
        if(!Guid.TryParseExact(args[1],"N",out profileId)||!Guid.TryParseExact(args[2],"N",out requestId))throw new InvalidDataException("Invalid installation request.");
        Updater.LocalData=ElevatedDataFolder(args[3]);
        string result=Path.Combine(Updater.Home,"switch-result-"+args[2]+".txt");
        try{var store=new ProfileStore(Updater.Home,true);if(store.RecoveryPending)store.Recover();else {var target=store.Load(args[1]);RequireSilver(target);store.Activate(target);}File.WriteAllText(result,"OK");Environment.Exit(0);}
        catch(Exception ex){File.WriteAllText(result,ex.Message);Environment.Exit(1);}
        return true;
    }
    static bool HandleElevatedRemoval(string[] args) {
        Guid requestId;
        if(!Guid.TryParseExact(args[1],"N",out requestId))throw new InvalidDataException("Invalid removal request.");
        Updater.LocalData=ElevatedDataFolder(args[2]);
        string result=Path.Combine(Updater.Home,"switch-result-"+args[1]+".txt");
        try{RemoveLeftovers(RemovalFolder(new ProfileStore(Updater.Home,true)));File.WriteAllText(result,"OK");Environment.Exit(0);}
        catch(Exception ex){File.WriteAllText(result,ex.Message);Environment.Exit(1);}
        return true;
    }
    internal static string ElevatedDataFolder(string value) {
        // Only a local, fully qualified folder that already holds launcher data is accepted.
        string full;
        try{full=Path.GetFullPath(value).TrimEnd('\\');}catch(Exception){throw new InvalidDataException("Invalid installation request.");}
        if(!Path.IsPathRooted(value)||value.StartsWith(@"\\")||!String.Equals(full,value.TrimEnd('\\'),StringComparison.OrdinalIgnoreCase)||
            !File.Exists(Path.Combine(full,"TPF2-MP","OfficialLauncher","profiles.json")))
            throw new InvalidDataException("Invalid installation request.");
        return full;
    }
    public static void RequireSilver(Profile target) {
        if(target==null||target.Channel!="official")throw new InvalidDataException("Only Silver can be installed. Local backups are kept for recovery only.");
    }
    public static async Task Activate(ProfileStore store,Profile target) {
        if(!store.RecoveryPending)RequireSilver(target);
        if(!NeedsElevation(store.State.GameFolder)){await Task.Run(()=>{if(store.RecoveryPending)store.Recover();else store.Activate(target);});return;}
        await RunElevated(request=>"--activate "+target.Id+" "+request,"Installation with administrator permissions was cancelled.");
        store.State=ProfileStore.Read<ProfileState>(Path.Combine(store.Root,"profiles.json"));
        if(store.State.Active!=target.Id||!store.Matches(target,store.State.GameFolder))throw new IOException("Could not verify installation with administrator permissions.");
    }
    static bool NeedsElevation(string game) {
        string probe=Path.Combine(game,".tpf2-launcher-"+Guid.NewGuid().ToString("N")+".tmp");
        try{using(File.Create(probe)){}File.Delete(probe);return false;}
        catch(UnauthorizedAccessException){return true;}
    }
    // Runs this helper again as administrator; the elevated copy reports errors through a result file.
    static async Task RunElevated(Func<string,string> arguments,string cancelled) {
        string request=Guid.NewGuid().ToString("N"),result=Path.Combine(Updater.Home,"switch-result-"+request+".txt");
        var info=new ProcessStartInfo(Application.ExecutablePath,arguments(request)+" \""+Updater.LocalData+"\""){UseShellExecute=true,Verb="runas"};
        try {
            using(var process=Process.Start(info)){
                await Task.Run(()=>process.WaitForExit());
                if(process.ExitCode!=0)throw new InvalidOperationException(File.Exists(result)?File.ReadAllText(result):cancelled);
            }
        } finally { if(File.Exists(result))File.Delete(result); }
    }
    public static void ValidateMsi(string package,Release release) {
        dynamic installer=Activator.CreateInstance(Type.GetTypeFromProgID("WindowsInstaller.Installer"));
        dynamic database=installer.OpenDatabase(package,0);
        dynamic view=database.OpenView("SELECT `Property`, `Value` FROM `Property`");view.Execute();
        string version=null,upgrade=null,name=null;
        for(dynamic row=view.Fetch();row!=null;row=view.Fetch()) {
            string property=row.StringData[1];string value=row.StringData[2];
            if(property=="ProductVersion")version=value;if(property=="UpgradeCode")upgrade=value;if(property=="ProductName")name=value;
        }
        view.Close();
        if(!Updater.SameVersion(version,release.Number.ToString())||upgrade!=UpgradeCode||name!="TpF2 Multiplayer")throw new InvalidDataException("MSI product metadata does not match the selected release.");
    }
    public static string StageMsi(Release release,string package,string directory) {
        // Windows Installer's service can fail to open the per-user download
        // cache (1619). Use a separate temporary input and verify the copy.
        Updater.Verify(release,package);
        Directory.CreateDirectory(directory);
        string staged=Path.Combine(directory,"TpF2Multiplayer.msi");
        File.Copy(package,staged,false);
        Updater.Verify(release,staged);
        return staged;
    }
    static string RepairOptions(string package) {
        dynamic installer=Activator.CreateInstance(Type.GetTypeFromProgID("WindowsInstaller.Installer"));
        dynamic database=installer.OpenDatabase(package,0);
        dynamic view=database.OpenView("SELECT `Value` FROM `Property` WHERE `Property` = 'ProductCode'");view.Execute();
        dynamic row=view.Fetch();
        if(row==null)throw new InvalidDataException("MSI product code is missing.");
        string productCode=row.StringData[1];view.Close();
        // Only reinstall an actually registered product; stale mod registry data is insufficient.
        return installer.ProductState[productCode]==5 ? " REINSTALL=ALL REINSTALLMODE=amus" : "";
    }
    internal static void CleanStage(string directory) {
        try {
            string full = Path.GetFullPath(directory);
            string parent = Path.GetDirectoryName(full);
            if (!String.Equals(parent, Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                || !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(full), @"\Atpf2-(?:base-)?[0-9a-f]{12}\z")) return;
            if (!Directory.Exists(full)) return;
            CheckCleanupTree(full);
            Directory.Delete(full, true);
        } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    static void CheckCleanupTree(string path) {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked cache path.");
        if (Directory.Exists(path)) foreach (string child in Directory.GetFileSystemEntries(path)) CheckCleanupTree(child);
    }
    internal static void CleanDownloads(string root) {
        string directory = Path.Combine(Path.GetFullPath(root), "Downloads");
        if (!Directory.Exists(directory)) return;
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0 || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return;
        foreach (string file in Directory.GetFiles(directory)) {
            if (!System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(file), @"\ATpF2Multiplayer-[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?\.msi\z")) continue;
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
            File.Delete(file);
        }
    }
    internal static void PreserveStockAudio(string game) {
        string original=Path.Combine(game,"alut_real.dll"),loader=Path.Combine(game,"alut.dll");
        if(File.Exists(original)) {
            if(ProfileStore.Hash(original)!=StockAudioHash)throw new InvalidOperationException("The original audio DLL is modified. Verify the game files in Steam before installing multiplayer.");
            return;
        }
        if(!File.Exists(loader)||ProfileStore.Hash(loader)!=StockAudioHash)
            throw new InvalidOperationException("The original audio DLL is missing. Verify the game files in Steam before installing multiplayer.");
        // Removing a registered older MSI can delete Steam's restored alut.dll
        // before the new installer's PreserveStockAlut action gets to run.
        File.Copy(loader,original,false);
        if(ProfileStore.Hash(original)!=StockAudioHash)throw new IOException("Could not verify the original audio DLL backup.");
    }
    public static async Task InstallBase(Release release,Action<string,int> progress) {
        Updater.RequireClosed();string game=Updater.GameFolder();
        string package=await Updater.Download(release,p=>progress("Downloading multiplayer…",p));ValidateMsi(package,release);
        progress("Setting up multiplayer…",0);
        await Task.Run(()=>Updater.Backup(game,Updater.RegistryValue("Version")??"First installation"));
        Updater.RequireClosed();
        PreserveStockAudio(game);
        string stage=Path.Combine(Path.GetTempPath(),"tpf2-base-"+Guid.NewGuid().ToString("N").Substring(0,12));
        try {
        package=StageMsi(release,package,stage);
        string log=Path.Combine(Updater.Home,"base-install-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".log");
        var info=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"msiexec.exe"),"/i \""+package+"\" /passive /norestart MSIRESTARTMANAGERCONTROL=Disable INSTALLFOLDER=\""+game+"\""+RepairOptions(package)+" /L*v \""+log+"\""){UseShellExecute=true,Verb="runas"};
        using(var process=Process.Start(info)){
            await Task.Run(()=>process.WaitForExit());
            if(process.ExitCode==3010)throw new InvalidOperationException("Base installation complete. Restart Windows, then reopen the launcher.");
            if(process.ExitCode!=0)throw new InvalidOperationException("Base installation incomplete ("+process.ExitCode+"). Log: "+log);
        }
        if(!Updater.SameVersion(Updater.RegistryValue("Version"),release.Number.ToString())||!ProfileStore.HasModFiles(game))throw new InvalidOperationException("Could not verify the base installation.");
        } finally { CleanStage(stage); }
    }
    static string[] RegisteredProducts() {
        dynamic installer=Activator.CreateInstance(Type.GetTypeFromProgID("WindowsInstaller.Installer"));
        dynamic related=installer.RelatedProducts[UpgradeCode];
        var products=new List<string>();
        for(int i=0;i<related.Count;i++) {
            string code=related.Item[i];
            if(Regex.IsMatch(code??"",@"\A\{[0-9A-Fa-f]{8}(?:-[0-9A-Fa-f]{4}){3}-[0-9A-Fa-f]{12}\}\z"))products.Add(code);
        }
        return products.ToArray();
    }
    static string RemovalFolder(ProfileStore store) {
        if(store.RecoveryPending)throw new InvalidOperationException("Restore the interrupted installation first.");
        Updater.RequireClosed();
        string game=Updater.GameFolder();
        if(store.State!=null&&!String.Equals(game,store.State.GameFolder,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Existing backups belong to a different game folder.");
        return game;
    }
    internal static bool HasLeftovers(string game) {
        return File.Exists(Path.Combine(game,"alut_real.dll"))||ProfileStore.Inventory(game).Any(p=>!String.Equals(p,"alut.dll",StringComparison.OrdinalIgnoreCase));
    }
    // Silver's MSI restores the stock audio DLL on removal. Files from releases the launcher
    // switched to afterwards are not part of that MSI, so all launcher-managed files are removed too.
    // Returns true when Windows needs a restart to finish.
    public static async Task<bool> Uninstall(ProfileStore store,Action<string> progress) {
        string game=RemovalFolder(store);
        var products=RegisteredProducts();
        if(products.Length==0&&!HasLeftovers(game))throw new InvalidOperationException("Multiplayer is not installed in this game folder.");
        // Silver's registry entry disappears with the MSI; keep resolving the same folder afterwards.
        Directory.CreateDirectory(Updater.Home);File.WriteAllText(Path.Combine(Updater.Home,"game-folder.txt"),game);
        progress("Backing up mod files…");
        await Task.Run(()=>Updater.Backup(game,Updater.RegistryValue("Version")??"Before removal"));
        bool restart=false;
        foreach(string product in products) {
            Updater.RequireClosed();
            progress("Removing multiplayer…");
            string log=Path.Combine(Updater.Home,"uninstall-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".log");
            var info=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"msiexec.exe"),"/x "+product+" /passive /norestart MSIRESTARTMANAGERCONTROL=Disable /L*v \""+log+"\""){UseShellExecute=true,Verb="runas"};
            using(var process=Process.Start(info)){
                await Task.Run(()=>process.WaitForExit());
                // 1605: the product is already gone.
                if(process.ExitCode==3010)restart=true;
                else if(process.ExitCode!=0&&process.ExitCode!=1605)throw new InvalidOperationException("Removal incomplete ("+process.ExitCode+"). Log: "+log);
            }
        }
        progress("Cleaning up…");
        RemovalFolder(store);
        if(NeedsElevation(game))await RunElevated(request=>"--remove "+request,"Removal with administrator permissions was cancelled.");
        else await Task.Run(()=>RemoveLeftovers(game));
        if(ProfileStore.HasModFiles(game)||HasLeftovers(game))throw new IOException("Could not verify the removal. Verify the game files in Steam.");
        store.Forget();
        return restart;
    }
    internal static void RemoveLeftovers(string game,string stockHash=StockAudioHash) {
        if((File.GetAttributes(game)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Linked game folders cannot be modified.");
        string original=Path.Combine(game,"alut_real.dll"),loader=Path.Combine(game,"alut.dll");
        bool stock=File.Exists(loader)&&ProfileStore.Hash(loader)==stockHash;
        if(File.Exists(original)&&(File.GetAttributes(original)&FileAttributes.ReparsePoint)==0&&ProfileStore.Hash(original)==stockHash) {
            if(!stock) {
                string temp=loader+".launcher-"+Guid.NewGuid().ToString("N")+".tmp";
                try {
                    File.Copy(original,temp);
                    if(ProfileStore.Hash(temp)!=stockHash)throw new IOException("Could not verify the original audio DLL.");
                    if(File.Exists(loader))File.Replace(temp,loader,null);else File.Move(temp,loader);
                } finally { if(File.Exists(temp))File.Delete(temp); }
                stock=true;
            }
            File.Delete(original);
        }
        // alut.dll itself is a game file; only its multiplayer replacement is undone above.
        foreach(string relative in ProfileStore.Inventory(game).Where(p=>!String.Equals(p,"alut.dll",StringComparison.OrdinalIgnoreCase)))
            File.Delete(ProfileStore.SafePath(game,relative));
        foreach(string folder in new[]{@"mods\mp_lockstep_1",@"netpunch\_internal","netpunch","plugins"})
            RemoveEmptyFolders(Path.Combine(game,folder));
        if(!stock)throw new InvalidOperationException("Multiplayer files were removed, but the original audio DLL could not be restored. Verify the game files in Steam.");
    }
    static void RemoveEmptyFolders(string folder) {
        if(!Directory.Exists(folder)||(File.GetAttributes(folder)&FileAttributes.ReparsePoint)!=0)return;
        foreach(string child in Directory.GetDirectories(folder))RemoveEmptyFolders(child);
        if(Directory.GetFileSystemEntries(folder).Length==0)Directory.Delete(folder);
    }
}
