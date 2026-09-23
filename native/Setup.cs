using System;
using System.IO;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Forms;

public static class LauncherSetup {
    public static bool HandleElevatedAction(string[] args) {
        if(args.Length!=3||args[0]!="--activate")return false;
        Guid profileId,requestId;
        if(!Guid.TryParseExact(args[1],"N",out profileId)||!Guid.TryParseExact(args[2],"N",out requestId))throw new InvalidDataException("Invalid installation request.");
        string result=Path.Combine(Updater.Home,"switch-result-"+args[2]+".txt");
        try{var store=new ProfileStore(Updater.Home,true);if(store.RecoveryPending)store.Recover();else {var target=store.Load(args[1]);RequireSilver(target);store.Activate(target);}File.WriteAllText(result,"OK");Environment.Exit(0);}
        catch(Exception ex){File.WriteAllText(result,ex.Message);Environment.Exit(1);}
        return true;
    }
    public static void RequireSilver(Profile target) {
        if(target==null||target.Channel!="official")throw new InvalidDataException("Only Silver can be installed. Legacy profiles are retained for recovery only.");
    }
    public static async Task Activate(ProfileStore store,Profile target) {
        if(!store.RecoveryPending)RequireSilver(target);
        bool needsElevation=false;
        string probe=Path.Combine(store.State.GameFolder,".tpf2-launcher-"+Guid.NewGuid().ToString("N")+".tmp");
        try{using(File.Create(probe)){}File.Delete(probe);}
        catch(UnauthorizedAccessException){needsElevation=true;}
        if(!needsElevation){await Task.Run(()=>{if(store.RecoveryPending)store.Recover();else store.Activate(target);});return;}
        string request=Guid.NewGuid().ToString("N"),result=Path.Combine(Updater.Home,"switch-result-"+request+".txt");
        var info=new ProcessStartInfo(Application.ExecutablePath,"--activate "+target.Id+" "+request){UseShellExecute=true,Verb="runas"};
        using(var process=Process.Start(info)){
            await Task.Run(()=>process.WaitForExit());
            if(process.ExitCode!=0)throw new InvalidOperationException(File.Exists(result)?File.ReadAllText(result):"Installation with administrator permissions was cancelled.");
        }
        store.State=ProfileStore.Read<ProfileState>(Path.Combine(store.Root,"profiles.json"));
        if(store.State.Active!=target.Id||!store.Matches(target,store.State.GameFolder))throw new IOException("Could not verify installation with administrator permissions.");
        if(File.Exists(result))File.Delete(result);
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
        if(!Updater.SameVersion(version,release.Number.ToString())||upgrade!="{80DBF679-F058-410E-9BAD-87731AC96633}"||name!="TpF2 Multiplayer")throw new InvalidDataException("MSI product metadata does not match the selected release.");
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
    public static async Task InstallBase(Release release,Action<int> progress) {
        if(release.Channel!="official")throw new InvalidOperationException("Install the Silver multiplayer base first.");
        Updater.RequireClosed();string game=Updater.GameFolder();
        string package=await Updater.Download(release,progress);ValidateMsi(package,release);
        await Task.Run(()=>Updater.Backup(game,Updater.RegistryValue("Version")??"First installation"));
        Updater.RequireClosed();
        string stage=Path.Combine(Path.GetTempPath(),"tpf2-base-"+Guid.NewGuid().ToString("N").Substring(0,12));
        try {
        package=StageMsi(release,package,stage);
        string log=Path.Combine(Updater.Home,"base-install-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".log");
        var info=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"msiexec.exe"),"/i \""+package+"\" /passive /norestart MSIRESTARTMANAGERCONTROL=Disable INSTALLFOLDER=\""+game+"\" /L*v \""+log+"\""){UseShellExecute=true,Verb="runas"};
        using(var process=Process.Start(info)){
            await Task.Run(()=>process.WaitForExit());
            if(process.ExitCode==3010)throw new InvalidOperationException("Base installation complete. Restart Windows, then reopen the launcher.");
            if(process.ExitCode!=0)throw new InvalidOperationException("Base installation incomplete ("+process.ExitCode+"). Log: "+log);
        }
        if(!Updater.SameVersion(Updater.RegistryValue("Version"),release.Number.ToString())||!File.Exists(Path.Combine(game,"alut_real.dll")))throw new InvalidOperationException("Could not verify the base installation.");
        } finally { CleanStage(stage); }
    }
}
