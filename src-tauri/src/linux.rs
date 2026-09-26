//! The Linux backend: the actions the Windows C# helper answers, for Transport
//! Fever 2 on Linux. It speaks the same JSON as the helper (progress events,
//! then `data`), so the web UI is the same on both systems.
//!
//! Linux runs the game two ways, and each has its own installer in every release
//! of silver2127/tpf2-multiplayer:
//!  - NATIVE (Steam's Linux build): `tpf2mp-linux-<v>-native.tar.gz`, whose
//!    `install.sh` puts the loader in `<data home>/tpf2mp` and the mod in the game,
//!    and (with --patch-runsh) adds the preload block to the game's run.sh;
//!  - PROTON (the Windows game under Proton): `TpF2Multiplayer-files.zip`, installed
//!    by the release's `install_proton.py` (the same files the MSI installs, plus the
//!    Wine repair of the lobby).
//! The launcher finds the game and the way Steam runs it, downloads and verifies
//! the package against the SHA-256 GitHub publishes for it, and runs the release's
//! own installer. It never writes the game's files itself.

use serde_json::{json, Value};
use sha2::{Digest, Sha256};
use std::fs;
use std::io::{BufRead, BufReader, Read, Write};
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::time::Duration;
use tauri::{AppHandle, Emitter};

pub const REPOSITORY: &str = "silver2127/tpf2-multiplayer";
/// Where the packages of v0.7.0.6 and later live, under the mod release's tag (the
/// mod's release then carries only the launchers). Older releases keep theirs in
/// REPOSITORY; a tag the packages repo does not have falls back to that.
pub const PACKAGES_REPOSITORY: &str = "silver2127/tpf2-multiplayer-packages";
const APP_ID: &str = "1066780";
const GAME_FOLDER: &str = "Transport Fever 2";
const USER_AGENT: &str = "TPF2-Launcher-Linux/1.1";
const MAX_PACKAGE: u64 = 512 << 20;
const PROTON_FILES: &str = "TpF2Multiplayer-files.zip";
const PROTON_INSTALLER: &str = "install_proton.py";
const PROTON_MANIFEST: &str = ".tpf2mp-proton-manifest.json";

// ---- paths ----------------------------------------------------------------------

fn home() -> PathBuf {
    std::env::var_os("HOME").map(PathBuf::from).unwrap_or_else(|| PathBuf::from("/"))
}
fn xdg_data_home() -> PathBuf {
    match std::env::var("XDG_DATA_HOME") {
        Ok(v) if v.starts_with('/') => PathBuf::from(v),
        _ => home().join(".local/share"),
    }
}
/// The launcher's own folder: settings, downloads (the backups of this port).
pub fn launcher_home() -> PathBuf {
    xdg_data_home().join("tpf2mp-launcher")
}

#[derive(Clone, Copy, PartialEq, Debug)]
pub enum SteamKind { Native, Snap, Flatpak }

#[derive(Clone, Copy, PartialEq, Debug)]
pub enum Mode { Native, Proton }
impl Mode {
    fn name(self) -> &'static str { match self { Mode::Native => "native", Mode::Proton => "proton" } }
    fn from_name(v: &str) -> Option<Mode> { match v { "native" => Some(Mode::Native), "proton" => Some(Mode::Proton), _ => None } }
}

/// The kind of Steam a path belongs to (tpf2mp_paths.sh `tpf2mp_steam_kind`).
pub fn steam_kind(path: &Path) -> SteamKind {
    let h = home();
    if path.starts_with(h.join("snap/steam")) { SteamKind::Snap }
    else if path.starts_with(h.join(".var/app/com.valvesoftware.Steam")) { SteamKind::Flatpak }
    else { SteamKind::Native }
}
/// The XDG_DATA_HOME the game sees (tpf2mp_paths.sh `tpf2mp_data_home`).
pub fn game_data_home(kind: SteamKind) -> PathBuf {
    match kind {
        SteamKind::Snap => home().join("snap/steam/common/.local/share"),
        SteamKind::Flatpak => home().join(".var/app/com.valvesoftware.Steam/data"),
        SteamKind::Native => xdg_data_home(),
    }
}

fn steam_roots() -> Vec<PathBuf> {
    let h = home();
    let mut out: Vec<PathBuf> = Vec::new();
    for c in [
        xdg_data_home().join("Steam"), h.join(".local/share/Steam"), h.join(".steam/root"), h.join(".steam/steam"),
        h.join(".steam/debian-installation"), h.join("snap/steam/common/.local/share/Steam"),
        h.join(".var/app/com.valvesoftware.Steam/.local/share/Steam"), h.join(".var/app/com.valvesoftware.Steam/data/Steam"),
    ] {
        if !(c.join("steamapps").is_dir() || c.join("userdata").is_dir()) { continue; }
        let real = fs::canonicalize(&c).unwrap_or(c);
        if !out.contains(&real) { out.push(real); }
    }
    out
}

/// Every `"path"` of a libraryfolders.vdf (the root is always a library too).
pub fn vdf_paths(text: &str) -> Vec<PathBuf> {
    text.lines().filter_map(|line| {
        let parts: Vec<&str> = line.split('"').collect();
        // "path"		"/home/x/Steam"  ->  ["", "path", "\t\t", "/home/x/Steam", ""]
        if parts.len() >= 4 && parts[1] == "path" { Some(PathBuf::from(parts[3].replace("\\\\", "\\"))) } else { None }
    }).collect()
}
/// A quoted `"key"  "value"` of a VDF/ACF text, the first one.
pub fn vdf_value(text: &str, key: &str) -> Option<String> {
    for line in text.lines() {
        let parts: Vec<&str> = line.split('"').collect();
        if parts.len() >= 4 && parts[1].eq_ignore_ascii_case(key) { return Some(parts[3].to_string()); }
    }
    None
}

/// `mode` is what this launcher installs and checks: the player's choice (Settings >
/// Game type), else what Steam runs (`steam_mode`). SWITCHING (the user: "the linux
/// launcher should be able to switch between proton and native as well"): which
/// build runs is Steam's choice, per game, under Properties > Compatibility, and
/// Steam downloads the other build when it changes. The launcher does not edit
/// Steam's config (Steam rewrites it while it runs); it reads which build Steam is
/// set to run and holds install and play until that matches the choice.
#[derive(Clone, Debug)]
pub struct Game { pub folder: PathBuf, pub kind: SteamKind, pub mode: Mode, pub steam_mode: Mode }

fn make_game(folder: PathBuf, library: PathBuf, settings: &Settings) -> Game {
    let steam_mode = detect_mode(&folder, &library);
    Game { mode: settings.mode_choice.unwrap_or(steam_mode), steam_mode, kind: steam_kind(&folder), folder }
}
/// The build this mode needs is on disk (Steam has finished switching).
pub fn build_present(game: &Game) -> bool {
    game.folder.join(match game.mode { Mode::Native => "TransportFever2", Mode::Proton => "TransportFever2.exe" }).is_file()
}
/// What the player has to do in Steam before this mode can be installed or played, if anything.
pub fn mode_note(game: &Game) -> Option<String> {
    if game.mode != game.steam_mode {
        return Some(match game.mode {
            Mode::Proton => "Steam runs the native Linux build. To play with Proton: in Steam, right-click Transport Fever 2 > Properties > Compatibility, tick \"Force the use of a specific Steam Play compatibility tool\", choose Proton, and let Steam update the game.".into(),
            Mode::Native => "Steam runs the Windows build with Proton. To play natively: in Steam, right-click Transport Fever 2 > Properties > Compatibility, untick \"Force the use of a specific Steam Play compatibility tool\", and let Steam update the game.".into(),
        });
    }
    if !build_present(game) {
        return Some("Steam has not finished downloading this build of the game yet. Let Steam update Transport Fever 2, then try again.".into());
    }
    None
}

fn has_game(folder: &Path) -> bool {
    folder.join("TransportFever2").is_file() || folder.join("TransportFever2.exe").is_file()
}
/// Native unless Steam runs the Windows build: an appmanifest that overrides the
/// platform to Windows, or a folder that only has the .exe.
pub fn detect_mode(folder: &Path, library: &Path) -> Mode {
    let acf = fs::read_to_string(library.join("steamapps").join(format!("appmanifest_{APP_ID}.acf"))).unwrap_or_default();
    if vdf_value(&acf, "platform_override_source").is_some_and(|v| v.eq_ignore_ascii_case("windows")) { return Mode::Proton; }
    if folder.join("TransportFever2").is_file() { Mode::Native } else { Mode::Proton }
}
fn library_of(folder: &Path) -> PathBuf {
    // <library>/steamapps/common/<game>
    folder.parent().and_then(Path::parent).and_then(Path::parent).map(Path::to_path_buf).unwrap_or_else(|| folder.to_path_buf())
}
pub fn find_game(settings: &Settings) -> Result<Game, String> {
    if let Some(chosen) = &settings.game_folder {
        let folder = PathBuf::from(chosen);
        if has_game(&folder) {
            let library = library_of(&folder);
            return Ok(make_game(folder, library, settings));
        }
    }
    for root in steam_roots() {
        let mut libraries = vec![root.clone()];
        for vdf in [root.join("steamapps/libraryfolders.vdf"), root.join("config/libraryfolders.vdf")] {
            if let Ok(text) = fs::read_to_string(vdf) { libraries.extend(vdf_paths(&text)); }
        }
        for library in libraries {
            let acf = fs::read_to_string(library.join("steamapps").join(format!("appmanifest_{APP_ID}.acf"))).unwrap_or_default();
            let dir = vdf_value(&acf, "installdir").unwrap_or_else(|| GAME_FOLDER.to_string());
            let folder = library.join("steamapps/common").join(dir);
            if has_game(&folder) {
                return Ok(make_game(folder, library, settings));
            }
        }
    }
    Err("Game folder not found. Use Select game folder.".into())
}

// ---- settings -----------------------------------------------------------------

#[derive(Default, Clone)]
pub struct Settings { pub experimental: bool, pub backup_limit: u32, pub game_folder: Option<String>, pub mode_choice: Option<Mode> }
fn settings_file() -> PathBuf { launcher_home().join("settings.json") }
pub fn load_settings() -> Settings {
    let v: Value = fs::read_to_string(settings_file()).ok().and_then(|t| serde_json::from_str(&t).ok()).unwrap_or(Value::Null);
    let limit = v["backupLimit"].as_u64().map(|n| n as u32).filter(|n| matches!(n, 0 | 1 | 3 | 5 | 10)).unwrap_or(5);
    Settings {
        experimental: v["experimental"].as_bool().unwrap_or(false),
        backup_limit: limit,
        game_folder: v["gameFolder"].as_str().map(str::to_owned),
        mode_choice: v["gameMode"].as_str().and_then(Mode::from_name),
    }
}
fn save_settings(s: &Settings) -> Result<(), String> {
    let dir = launcher_home();
    fs::create_dir_all(&dir).map_err(|e| e.to_string())?;
    let value = json!({"experimental": s.experimental, "backupLimit": s.backup_limit, "gameFolder": s.game_folder, "gameMode": s.mode_choice.map(Mode::name)});
    let tmp = dir.join(format!("settings.json.{}.tmp", std::process::id()));
    fs::write(&tmp, serde_json::to_vec_pretty(&value).unwrap()).map_err(|e| e.to_string())?;
    fs::rename(&tmp, settings_file()).map_err(|e| e.to_string())
}

// ---- the running game -----------------------------------------------------------

/// Transport Fever 2 native (comm "TransportFever2") or under Proton (a Wine
/// process whose command line names TransportFever2.exe).
pub fn game_running() -> bool {
    let Ok(dir) = fs::read_dir("/proc") else { return false };
    for entry in dir.flatten() {
        let name = entry.file_name();
        if !name.to_string_lossy().bytes().all(|b| b.is_ascii_digit()) { continue; }
        let comm = fs::read_to_string(entry.path().join("comm")).unwrap_or_default();
        if comm.trim_end() == "TransportFever2" || comm.trim_end().starts_with("TransportFever2") { return true; }
        let cmdline = fs::read(entry.path().join("cmdline")).unwrap_or_default();
        if cmdline.split(|&b| b == 0).any(|arg| {
            let a = String::from_utf8_lossy(arg);
            a.ends_with("TransportFever2.exe") || a.ends_with("/TransportFever2")
        }) { return true; }
    }
    false
}
fn require_closed() -> Result<(), String> {
    if game_running() { Err("Close all Transport Fever 2 windows first.".into()) } else { Ok(()) }
}

// ---- what is installed ------------------------------------------------------------

/// The installed multiplayer version for this game and mode, or None.
pub fn installed_version(game: &Game) -> Option<String> {
    match game.mode {
        Mode::Native => {
            let manifest = fs::read_to_string(game_data_home(game.kind).join("tpf2mp/tpf2mp_install.txt")).ok()?;
            let mut version = None; let mut for_game = None;
            for line in manifest.lines() {
                if let Some(v) = line.strip_prefix("version\t") { version = Some(v.trim().to_string()); }
                if let Some(g) = line.strip_prefix("game\t") { for_game = Some(PathBuf::from(g.trim())); }
            }
            let same = for_game.map(|g| fs::canonicalize(&g).unwrap_or(g) == fs::canonicalize(&game.folder).unwrap_or(game.folder.clone())).unwrap_or(true);
            if !same || !game.folder.join("mods/mp_lockstep_1/mod.lua").is_file() { return None; }
            version.filter(|v| parse_version(v).is_some())
        }
        Mode::Proton => {
            let manifest: Value = serde_json::from_str(&fs::read_to_string(game.folder.join(PROTON_MANIFEST)).ok()?).ok()?;
            manifest["version"].as_str().map(str::to_owned).filter(|v| parse_version(v).is_some())
        }
    }
}

/// NO STEAM LAUNCH OPTIONS (the user, 2026-09-26: the launcher should not need
/// them). A native install patches the game's run.sh (`install.sh --patch-runsh`):
/// Steam starts the native game through run.sh, so its preload block (tpf2mp_paths.sh
/// TPF2MP_RUNSH_BLOCK) loads the mod on every launch, and inside the Steam Runtime
/// container too (tpf2-multiplayer e4c0e32). A Steam update or a file check restores
/// run.sh; `play` then puts the block back from the cached release first.
pub const RUNSH_MARK: &str = "# tpf2mp (Linux port): preload the multiplayer loader when it is installed.";
/// Native only: does the game's run.sh still carry the preload block?
pub fn start_script_ok(game: &Game) -> bool {
    fs::read_to_string(game.folder.join("run.sh")).map(|t| t.lines().any(|l| l == RUNSH_MARK)).unwrap_or(false)
}

// ---- GitHub releases -----------------------------------------------------------

/// "0.7", "0.7.0.5", "v0.6.1" -> the numbers (two to four parts).
pub fn parse_version(value: &str) -> Option<Vec<u64>> {
    let v = value.strip_prefix('v').unwrap_or(value);
    let parts: Vec<&str> = v.split('.').collect();
    if !(2..=4).contains(&parts.len()) { return None; }
    parts.iter().map(|p| if !p.is_empty() && p.len() <= 9 && p.bytes().all(|b| b.is_ascii_digit()) { p.parse().ok() } else { None }).collect()
}
fn version_string(numbers: &[u64]) -> String { numbers.iter().map(u64::to_string).collect::<Vec<_>>().join(".") }

pub fn is_experimental(release: &Value) -> bool {
    if release["prerelease"].as_bool().unwrap_or(false) { return true; }
    let name = release["name"].as_str().unwrap_or("").to_ascii_lowercase();
    name.split(|c: char| !c.is_ascii_alphanumeric()).any(|w| matches!(w, "experimental" | "alpha" | "beta" | "preview" | "rc"))
}

#[derive(Debug)]
pub struct Package { pub name: String, pub url: String, pub sha256: String, pub size: u64 }

fn asset(release: &Value, name: &str) -> Result<Package, String> {
    let tag = release["tag_name"].as_str().unwrap_or("");
    let repo = if release["_assetsRepo"] == PACKAGES_REPOSITORY { PACKAGES_REPOSITORY } else { REPOSITORY };
    let matches: Vec<&Value> = release["assets"].as_array().map(|a| a.iter().filter(|x| x["name"] == name).collect()).unwrap_or_default();
    if matches.len() != 1 { return Err(format!("The release has no {name}.")); }
    let a = matches[0];
    let url = a["browser_download_url"].as_str().unwrap_or("");
    let digest = a["digest"].as_str().unwrap_or("");
    let size = a["size"].as_u64().unwrap_or(0);
    let hex = digest.strip_prefix("sha256:").unwrap_or("");
    if url != format!("https://github.com/{repo}/releases/download/{tag}/{name}")
        || hex.len() != 64 || !hex.bytes().all(|b| b.is_ascii_hexdigit()) || size == 0 || size > MAX_PACKAGE {
        return Err("The download URL, size or SHA-256 checksum is missing or invalid.".into());
    }
    Ok(Package { name: name.into(), url: url.into(), sha256: hex.to_ascii_lowercase(), size })
}
/// The packages a release needs for this mode, or why it cannot be installed.
pub fn packages(release: &Value, mode: Mode, allow_experimental: bool) -> Result<Vec<Package>, String> {
    let tag = release["tag_name"].as_str().unwrap_or("");
    let Some(numbers) = parse_version(tag) else { return Err("This is not a supported Silver release.".into()) };
    if release["draft"].as_bool().unwrap_or(false) || (is_experimental(release) && !allow_experimental) {
        return Err("This is not a supported stable Silver release.".into());
    }
    let shown = tag.strip_prefix('v').unwrap_or(tag);
    match mode {
        Mode::Native => asset(release, &format!("tpf2mp-linux-{shown}-native.tar.gz"))
            .map_err(|_| "This release has no native Linux package. Use a newer release, or run the game with Proton.".to_string())
            .map(|p| { let _ = numbers; vec![p] }),
        Mode::Proton => Ok(vec![asset(release, PROTON_FILES)?, asset(release, PROTON_INSTALLER)?]),
    }
}
pub fn summary(release: &Value, mode: Mode, allow_experimental: bool) -> Value {
    let tag = release["tag_name"].as_str().unwrap_or("");
    let issue = packages(release, mode, allow_experimental).err();
    let body = release["body"].as_str().unwrap_or("");
    json!({
        "version": tag.strip_prefix('v').unwrap_or(tag),
        "date": release["published_at"],
        "notes": if body.trim().is_empty() { "No release notes available." } else { body },
        "experimental": is_experimental(release),
        "installable": issue.is_none(),
        "installationIssue": issue,
    })
}

fn agent() -> ureq::Agent {
    ureq::AgentBuilder::new().timeout_connect(Duration::from_secs(20)).timeout_read(Duration::from_secs(45)).user_agent(USER_AGENT).build()
}
fn github_json(path: &str) -> Result<Option<Value>, String> { github_json_in(REPOSITORY, path) }
fn github_json_in(repo: &str, path: &str) -> Result<Option<Value>, String> {
    match agent().get(&format!("https://api.github.com/repos/{repo}/{path}")).set("Accept", "application/vnd.github+json").call() {
        Ok(r) => r.into_json::<Value>().map(Some).map_err(|_| "Invalid response from GitHub.".into()),
        Err(ureq::Error::Status(404, _)) => Ok(None),
        Err(ureq::Error::Status(code, _)) => Err(format!("GitHub answered {code}. Try again later.")),
        Err(_) => Err("GitHub did not respond. Check your connection and try again.".into()),
    }
}
fn release_page(page: u32, count: u32) -> Result<Vec<Value>, String> {
    let v = github_json(&format!("releases?per_page={count}&page={page}"))?.ok_or("Empty response from GitHub.")?;
    v.as_array().cloned().ok_or_else(|| "Invalid response from GitHub.".into())
}
/// The packages repo's assets for one tag, when it has that release.
fn packages_for(tag: &str) -> Option<Value> {
    let r = github_json_in(PACKAGES_REPOSITORY, &format!("releases/tags/{tag}")).ok()??;
    if r["tag_name"] != tag || r["draft"].as_bool().unwrap_or(false) { return None; }
    r["assets"].as_array().filter(|a| !a.is_empty()).map(|a| Value::Array(a.clone()))
}
/// Every tag's assets in the packages repo, from one listing (the history's check).
fn packages_index() -> std::collections::HashMap<String, Value> {
    let mut index = std::collections::HashMap::new();
    for page in 1..=5 {
        let Ok(Some(Value::Array(releases))) = github_json_in(PACKAGES_REPOSITORY, &format!("releases?per_page=100&page={page}")) else { break };
        let n = releases.len();
        for r in releases {
            if let (Some(tag), Some(assets)) = (r["tag_name"].as_str(), r["assets"].as_array()) {
                if !r["draft"].as_bool().unwrap_or(false) && !assets.is_empty() { index.insert(tag.to_string(), Value::Array(assets.clone())); }
            }
        }
        if n < 100 { break; }
    }
    index
}
/// A mod release with its packages: from the packages repo when it has the tag.
pub fn with_packages(mut release: Value, assets: Option<Value>) -> Value {
    if let Some(assets) = assets {
        release["assets"] = assets;
        release["_assetsRepo"] = Value::from(PACKAGES_REPOSITORY);
    }
    release
}

/// A release of the mod, not of the launcher: launcher updates are their own
/// releases in the mod repo, tagged `launcher-v<version>`, never offered as the mod.
pub fn is_mod_release(release: &Value) -> bool {
    !release["draft"].as_bool().unwrap_or(false) && !release["tag_name"].as_str().unwrap_or("").starts_with("launcher-")
}
/// The newest mod release of the track, by publication date.
pub fn select_release(releases: &[Value], experimental: bool) -> Option<Value> {
    releases.iter().filter(|r| is_mod_release(r) && is_experimental(r) == experimental)
        .max_by(|a, b| a["published_at"].as_str().unwrap_or("").cmp(b["published_at"].as_str().unwrap_or(""))).cloned()
}
fn fetch(experimental: bool) -> Result<Option<Value>, String> {
    let mut all = Vec::new();
    for page in 1..=10 {
        let releases = release_page(page, 100)?;
        let n = releases.len();
        all.extend(releases);
        if n < 100 { break; }
        if page == 10 { return Err("Release list is too large to determine the latest version safely.".into()); }
    }
    Ok(select_release(&all, experimental).map(|r| {
        let tag = r["tag_name"].as_str().unwrap_or("").to_string();
        with_packages(r, packages_for(&tag))
    }))
}
fn fetch_version(version: &str) -> Result<Value, String> {
    parse_version(version).ok_or("Invalid version number.")?;
    for tag in [format!("v{version}"), version.to_string()] {
        if let Some(r) = github_json(&format!("releases/tags/{tag}"))? {
            let got = r["tag_name"].as_str().and_then(parse_version).map(|n| version_string(&n));
            if got.as_deref() != parse_version(version).map(|n| version_string(&n)).as_deref() {
                return Err("The requested release does not match.".into());
            }
            let tag = r["tag_name"].as_str().unwrap_or("").to_string();
            return Ok(with_packages(r, packages_for(&tag)));
        }
    }
    Err("This release is no longer available.".into())
}

// ---- download and verify ---------------------------------------------------------

fn progress(app: &AppHandle, text: &str, percent: u32) {
    let _ = app.emit("native-progress", json!({"kind": "progress", "text": text, "percent": percent}));
}
pub fn sha256_file(path: &Path) -> Result<String, String> {
    let mut file = fs::File::open(path).map_err(|e| e.to_string())?;
    let mut hash = Sha256::new();
    let mut buf = vec![0u8; 1 << 20];
    loop {
        let n = file.read(&mut buf).map_err(|e| e.to_string())?;
        if n == 0 { break; }
        hash.update(&buf[..n]);
    }
    Ok(format!("{:x}", hash.finalize()))
}
fn verified(path: &Path, package: &Package) -> Result<(), String> {
    if fs::metadata(path).map(|m| m.len()).unwrap_or(0) != package.size { return Err("The download is incomplete.".into()); }
    if sha256_file(path)? != package.sha256 { return Err("SHA-256 verification failed. Installation stopped.".into()); }
    Ok(())
}
fn sidecar(file: &Path) -> PathBuf { let mut name = file.as_os_str().to_owned(); name.push(".sha256"); PathBuf::from(name) }
/// A downloaded package still matching the SHA-256 it was verified against.
fn cached(version: &str, name: &str) -> Option<PathBuf> {
    let file = launcher_home().join("Downloads").join(version).join(name);
    let want = fs::read_to_string(sidecar(&file)).ok()?;
    (file.is_file() && sha256_file(&file).ok()? == want.trim()).then_some(file)
}
/// Download into the launcher's Downloads/<version>/ folder (a verified copy is reused).
fn download(app: &AppHandle, version: &str, package: &Package) -> Result<PathBuf, String> {
    let dir = launcher_home().join("Downloads").join(version);
    fs::create_dir_all(&dir).map_err(|e| e.to_string())?;
    let file = dir.join(&package.name);
    if file.is_file() && verified(&file, package).is_ok() { let _ = fs::write(sidecar(&file), &package.sha256); return Ok(file); }
    let partial = dir.join(format!("{}.{}.part", package.name, std::process::id()));
    let result = (|| {
        let response = agent().get(&package.url).timeout(Duration::from_secs(600)).call()
            .map_err(|_| format!("Could not download {}. Try again.", package.name))?;
        let mut reader = response.into_reader();
        let mut out = fs::File::create(&partial).map_err(|e| e.to_string())?;
        let (mut done, mut shown) = (0u64, u32::MAX);
        let mut buf = vec![0u8; 256 << 10];
        loop {
            let n = reader.read(&mut buf).map_err(|_| "The download was interrupted. Try again.".to_string())?;
            if n == 0 { break; }
            done += n as u64;
            if done > package.size { return Err("The download is larger than announced.".to_string()); }
            out.write_all(&buf[..n]).map_err(|e| e.to_string())?;
            let pct = ((done * 100) / package.size) as u32;
            if pct != shown { shown = pct; progress(app, &format!("Downloading {}…", package.name), pct); }
        }
        out.sync_all().ok();
        verified(&partial, package)?;
        fs::rename(&partial, &file).map_err(|e| e.to_string())?;
        fs::write(sidecar(&file), &package.sha256).map_err(|e| e.to_string())
    })();
    if result.is_err() { let _ = fs::remove_file(&partial); }
    result.map(|_| file)
}
/// Keep the newest `limit` downloaded versions (0 = all).
fn prune_downloads(limit: u32, keep: &str) {
    if limit == 0 { return; }
    let Ok(dir) = fs::read_dir(launcher_home().join("Downloads")) else { return };
    let mut versions: Vec<(Vec<u64>, PathBuf)> = dir.flatten().filter_map(|e| {
        let name = e.file_name().to_string_lossy().to_string();
        let meta = fs::symlink_metadata(e.path()).ok()?;
        if !meta.is_dir() || meta.file_type().is_symlink() { return None; }
        parse_version(&name).map(|v| (v, e.path()))
    }).collect();
    versions.sort_by(|a, b| b.0.cmp(&a.0));
    for (numbers, path) in versions.into_iter().skip(limit as usize) {
        if version_string(&numbers) != keep { let _ = fs::remove_dir_all(path); }
    }
}

// ---- installers -----------------------------------------------------------------

/// Run an installer, turning its output into progress lines; its last lines are the error.
fn run_installer(app: &AppHandle, mut command: Command, what: &str) -> Result<Vec<String>, String> {
    let mut child = command.stdin(Stdio::null()).stdout(Stdio::piped()).stderr(Stdio::piped()).spawn()
        .map_err(|e| format!("Could not start the {what}: {e}"))?;
    let stderr = child.stderr.take().unwrap();
    let errors = std::thread::spawn(move || BufReader::new(stderr).lines().map_while(Result::ok).collect::<Vec<_>>());
    let mut lines = Vec::new();
    for line in BufReader::new(child.stdout.take().unwrap()).lines().map_while(Result::ok) {
        let shown = line.trim();
        if !shown.is_empty() { progress(app, &shown.chars().take(160).collect::<String>(), 0); }
        lines.push(line);
    }
    let status = child.wait().map_err(|e| e.to_string())?;
    let errors = errors.join().unwrap_or_default();
    if !status.success() {
        let why = errors.iter().chain(lines.iter()).rev().map(|l| l.trim()).find(|l| !l.is_empty()).unwrap_or("no output");
        return Err(format!("The {what} stopped: {}", why.trim_start_matches("error: ")));
    }
    lines.extend(errors);
    Ok(lines)
}
fn unpack_native(tarball: &Path, version: &str) -> Result<(PathBuf, PathBuf), String> {
    let stage = launcher_home().join(format!("stage-{}", std::process::id()));
    let _ = fs::remove_dir_all(&stage);
    fs::create_dir_all(&stage).map_err(|e| e.to_string())?;
    let file = fs::File::open(tarball).map_err(|e| e.to_string())?;
    let mut archive = tar::Archive::new(flate2::read::GzDecoder::new(file));
    archive.set_preserve_permissions(true);
    for entry in archive.entries().map_err(|e| e.to_string())? {
        let mut entry = entry.map_err(|e| e.to_string())?;
        // unpack_in refuses absolute paths and "..": nothing lands outside the stage
        if !entry.unpack_in(&stage).map_err(|e| e.to_string())? { return Err("The package contains an unsafe path.".into()); }
    }
    let release = stage.join(format!("tpf2mp-linux-{version}"));
    if !release.join("install.sh").is_file() || !release.join("lib/libtpf2mp_boot.so").is_file() {
        let _ = fs::remove_dir_all(&stage);
        return Err("The package is not a TpF2 Multiplayer Linux release.".into());
    }
    Ok((stage, release))
}
fn install_native(app: &AppHandle, game: &Game, tarball: &Path, version: &str) -> Result<(), String> {
    let (stage, dir) = unpack_native(tarball, version)?;
    let mut cmd = Command::new("bash");
    cmd.arg(dir.join("install.sh")).arg("--game").arg(&game.folder).arg("--patch-runsh").current_dir(&dir);
    let result = run_installer(app, cmd, "installer");
    let _ = fs::remove_dir_all(&stage);
    result.map(|_| ())
}
/// A package of an installed version: the verified cached copy, else downloaded again.
fn package_file(app: &AppHandle, version: &str, mode: Mode, name: &str) -> Result<PathBuf, String> {
    if let Some(file) = cached(version, name) { return Ok(file); }
    let release = fetch_version(version)?;
    let package = packages(&release, mode, true)?.into_iter().find(|p| p.name == name)
        .ok_or_else(|| format!("Release {version} has no {name}."))?;
    download(app, version, &package)
}
/// Steam restored run.sh: put the preload block back from the installed release.
fn repair_start_script(app: &AppHandle, game: &Game, version: &str) -> Result<(), String> {
    progress(app, "Repairing the game's start script…", 0);
    let tarball = package_file(app, version, Mode::Native, &format!("tpf2mp-linux-{version}-native.tar.gz"))?;
    install_native(app, game, &tarball, version)?;
    if !start_script_ok(game) { return Err("Could not repair the game's start script. Verify the game files in Steam, then install again.".into()); }
    Ok(())
}
/// Remove multiplayer with the installed release's own uninstaller: native
/// `uninstall.sh` (the mod, the libraries, and run.sh back to Steam's), Proton
/// `install_proton.py --uninstall` (the game's own alut.dll back). Saves, logs and
/// the lobby's data stay, as on Windows.
fn uninstall(app: &AppHandle, settings: &Settings) -> Result<(), String> {
    require_closed()?;
    let game = find_game(settings)?;
    let version = installed_version(&game).ok_or("Multiplayer is not installed for this game type.")?;
    match game.mode {
        Mode::Native => {
            progress(app, "Removing multiplayer…", 0);
            let tarball = package_file(app, &version, Mode::Native, &format!("tpf2mp-linux-{version}-native.tar.gz"))?;
            require_closed()?;
            let (stage, dir) = unpack_native(&tarball, &version)?;
            let mut cmd = Command::new("bash");
            cmd.arg(dir.join("uninstall.sh")).arg("--game").arg(&game.folder).current_dir(&dir);
            let result = run_installer(app, cmd, "uninstaller");
            let _ = fs::remove_dir_all(&stage);
            result?;
        }
        Mode::Proton => {
            progress(app, "Removing multiplayer…", 0);
            let script = package_file(app, &version, Mode::Proton, PROTON_INSTALLER)?;
            require_closed()?;
            let mut cmd = Command::new("python3");
            cmd.arg(&script).arg("--uninstall").arg("--game-dir").arg(&game.folder);
            run_installer(app, cmd, "Proton uninstaller")?;
        }
    }
    if installed_version(&find_game(settings)?).is_some() { return Err("Could not verify that multiplayer was removed.".into()); }
    Ok(())
}
fn install(app: &AppHandle, settings: &mut Settings, version: &str) -> Result<(), String> {
    require_closed()?;
    let game = find_game(settings)?;
    if let Some(note) = mode_note(&game) { return Err(note); }
    let release = fetch_version(version)?;
    let shown = release["tag_name"].as_str().unwrap_or("").trim_start_matches('v').to_string();
    let pkgs = packages(&release, game.mode, settings.experimental)?;
    progress(app, "Downloading and verifying release…", 0);
    let mut files = Vec::new();
    for p in &pkgs { files.push(download(app, &shown, p)?); }
    require_closed()?;
    match game.mode {
        Mode::Native => {
            progress(app, "Installing multiplayer…", 0);
            install_native(app, &game, &files[0], &shown)?;
        }
        Mode::Proton => {
            if Command::new("python3").arg("--version").output().is_err() {
                return Err("Python 3 is needed to install for Proton. Install python3 with your package manager.".into());
            }
            progress(app, "Installing multiplayer for Proton…", 0);
            let mut cmd = Command::new("python3");
            cmd.arg(&files[1]).arg("--files-zip").arg(&files[0]).arg("--game-dir").arg(&game.folder);
            run_installer(app, cmd, "Proton installer")?;
        }
    }
    save_settings(settings)?;
    if installed_version(&find_game(settings)?).as_deref().and_then(parse_version).map(|v| version_string(&v))
        != parse_version(&shown).map(|v| version_string(&v)) {
        return Err("Could not verify the installation.".into());
    }
    prune_downloads(settings.backup_limit, &shown);
    Ok(())
}

// ---- actions ---------------------------------------------------------------------

fn open(target: &str) -> Result<(), String> {
    Command::new("xdg-open").arg(target).stdin(Stdio::null()).stdout(Stdio::null()).stderr(Stdio::null())
        .spawn().map(|_| ()).map_err(|_| "Could not open it: xdg-open is missing.".into())
}
fn status(settings: &Settings) -> Value {
    let game = find_game(settings).ok();
    let version = game.as_ref().and_then(installed_version);
    // Counted as installed while run.sh lacks the block: Play repairs it (installing
    // the latest release instead would change the player's version).
    let script_missing = matches!((&game, &version), (Some(g), Some(_)) if g.mode == Mode::Native && !start_script_ok(g));
    json!({
        "gameFolder": game.as_ref().map(|g| g.folder.display().to_string()),
        "installed": version.map(|v| json!({"version": v, "channel": "official"})),
        "running": game_running(),
        "recovery": false,
        "initialized": game.is_some(),
        "backupLimit": settings.backup_limit,
        "experimental": settings.experimental,
        "platform": "linux",
        "mode": game.as_ref().map(|g| g.mode.name()),
        "steamMode": game.as_ref().map(|g| g.steam_mode.name()),
        "modeChoice": settings.mode_choice.map(Mode::name).unwrap_or("auto"),
        "modeNote": game.as_ref().and_then(mode_note),
        "startScriptNote": if script_missing {
            Some("Steam restored the game's start script (after an update or a file check). Play puts multiplayer back before starting the game.")
        } else { None },
    })
}

/// One action, as the Windows helper runs it: `value` is its single argument.
pub fn run(app: &AppHandle, action: &str, value: Option<&str>) -> Result<Value, String> {
    let mut settings = load_settings();
    match action {
        "status" | "recover" => Ok(status(&settings)),
        "release-track" => {
            settings.experimental = match value { Some("1") => true, Some("0") => false, _ => return Err("Invalid release track.".into()) };
            save_settings(&settings)?;
            Ok(json!({"experimental": settings.experimental}))
        }
        "game-mode" => {
            settings.mode_choice = match value { Some("0") => None, Some("1") => Some(Mode::Native), Some("2") => Some(Mode::Proton), _ => return Err("Invalid game type.".into()) };
            save_settings(&settings)?;
            Ok(status(&settings))
        }
        "backup-limit" => {
            let limit: u32 = value.and_then(|v| v.parse().ok()).filter(|n| matches!(n, 0 | 1 | 3 | 5 | 10))
                .ok_or("Choose 1, 3, 5, 10 backups or unlimited.")?;
            settings.backup_limit = limit;
            save_settings(&settings)?;
            Ok(json!({"backupLimit": limit}))
        }
        "fetch" => {
            let mode = find_game(&settings).map(|g| g.mode).unwrap_or(Mode::Native);
            Ok(fetch(settings.experimental)?.map(|r| summary(&r, mode, settings.experimental)).unwrap_or(Value::Null))
        }
        "history" => {
            let page: u32 = value.and_then(|v| v.parse().ok()).filter(|p| (1..=10000).contains(p)).ok_or("Invalid history page.")?;
            let mode = find_game(&settings).map(|g| g.mode).unwrap_or(Mode::Native);
            let releases = release_page(page, 20)?;
            let index = packages_index();
            let entries: Vec<Value> = releases.iter().filter(|r| is_mod_release(r) && is_experimental(r) == settings.experimental)
                .map(|r| {
                    let tag = r["tag_name"].as_str().unwrap_or("");
                    summary(&with_packages(r.clone(), index.get(tag).cloned()), mode, settings.experimental)
                }).collect();
            Ok(json!({"entries": entries, "hasMore": releases.len() == 20}))
        }
        "install" => {
            let version = value.ok_or("Check the available release first.")?;
            install(app, &mut settings, version)?;
            Ok(status(&load_settings()))
        }
        "uninstall" => {
            uninstall(app, &settings)?;
            Ok(json!({"status": status(&settings), "restartRequired": false}))
        }
        "choose-folder" => {
            require_closed()?;
            use tauri_plugin_dialog::DialogExt;
            let Some(picked) = app.dialog().file().set_title("Select the Transport Fever 2 folder").blocking_pick_folder() else { return Ok(status(&settings)) };
            let folder = picked.into_path().map_err(|_| "Invalid folder.")?;
            if !has_game(&folder) { return Err("This folder does not contain Transport Fever 2.".into()); }
            settings.game_folder = Some(fs::canonicalize(&folder).unwrap_or(folder).display().to_string());
            save_settings(&settings)?;
            Ok(status(&settings))
        }
        "play" => {
            let game = find_game(&settings)?;
            if let Some(note) = mode_note(&game) { return Err(note); }
            let Some(version) = installed_version(&game) else { return Err("Install multiplayer before starting the game from this launcher.".into()) };
            require_closed()?;
            if game.mode == Mode::Native && !start_script_ok(&game) { repair_start_script(app, &game, &version)?; }
            open(&format!("steam://rungameid/{APP_ID}"))?;
            Ok(json!({"started": true}))
        }
        "game-folder" => { open(&find_game(&settings)?.folder.display().to_string())?; Ok(json!({"opened": true})) }
        "backups" => {
            let dir = launcher_home().join("Downloads");
            fs::create_dir_all(&dir).map_err(|e| e.to_string())?;
            open(&dir.display().to_string())?; Ok(json!({"opened": true}))
        }
        "release-link" => { open(value.ok_or("Invalid release link.")?)?; Ok(json!({"opened": true})) }
        "release-page" => { open(&format!("https://github.com/{REPOSITORY}/releases"))?; Ok(json!({"opened": true})) }
        _ => Err("Unknown launcher action.".into()),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test] fn versions() {
        assert_eq!(parse_version("v0.7.0.5"), Some(vec![0, 7, 0, 5]));
        assert_eq!(parse_version("0.7"), Some(vec![0, 7]));
        for bad in ["", "7", "v0.7.0.5.1", "0.x", "0..7", "0.7-rc1", "../0.7"] { assert!(parse_version(bad).is_none(), "{bad}"); }
    }
    #[test] fn library_folders() {
        let text = "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"/home/a/.local/share/Steam\"\n\t}\n\t\"1\"\n\t{\n\t\t\"path\"\t\t\"/mnt/games/SteamLibrary\"\n\t}\n}\n";
        assert_eq!(vdf_paths(text), vec![PathBuf::from("/home/a/.local/share/Steam"), PathBuf::from("/mnt/games/SteamLibrary")]);
        let acf = "\"AppState\"\n{\n\t\"installdir\"\t\t\"Transport Fever 2\"\n\t\"UserConfig\"\n\t{\n\t\t\"platform_override_source\"\t\t\"windows\"\n\t}\n}";
        assert_eq!(vdf_value(acf, "installdir").as_deref(), Some("Transport Fever 2"));
        assert_eq!(vdf_value(acf, "platform_override_source").as_deref(), Some("windows"));
    }
    fn release(tag: &str, name: &str, pre: bool, assets: &[&str]) -> Value {
        json!({"tag_name": tag, "name": name, "prerelease": pre, "draft": false, "published_at": "2026-09-26T19:00:00Z", "body": "notes",
               "assets": assets.iter().map(|a| json!({"name": a, "size": 10, "digest": format!("sha256:{}", "a".repeat(64)),
               "browser_download_url": format!("https://github.com/{REPOSITORY}/releases/download/{tag}/{a}")})).collect::<Vec<_>>()})
    }
    #[test] fn packages_per_mode() {
        let r = release("v0.7.0.5", "0.7.0.5", false, &["tpf2mp-linux-0.7.0.5-native.tar.gz", PROTON_FILES, PROTON_INSTALLER, "TpF2Multiplayer.msi"]);
        assert_eq!(packages(&r, Mode::Native, false).unwrap()[0].name, "tpf2mp-linux-0.7.0.5-native.tar.gz");
        assert_eq!(packages(&r, Mode::Proton, false).unwrap().len(), 2);
        let old = release("v0.6.1.19", "0.6.1.19", false, &[PROTON_FILES, PROTON_INSTALLER]);
        assert!(packages(&old, Mode::Native, false).unwrap_err().contains("no native Linux package"));
        let pre = release("v0.7.0.1", "0.7.0.1 preview", true, &[PROTON_FILES, PROTON_INSTALLER]);
        assert!(is_experimental(&pre) && packages(&pre, Mode::Proton, false).is_err() && packages(&pre, Mode::Proton, true).is_ok());
        let mut forged = r.clone();
        forged["assets"][0]["browser_download_url"] = json!("https://evil.example/tpf2mp-linux-0.7.0.5-native.tar.gz");
        assert!(packages(&forged, Mode::Native, false).is_err());
    }
    #[test] fn packages_repo_or_the_mod_repo() {
        // v0.7.0.6 on: the mod release has only the launchers, the packages repo the rest
        let bare = release("v0.7.0.6", "0.7.0.6", false, &["TPF2-Launcher-Setup.exe"]);
        assert!(packages(&bare, Mode::Native, false).is_err());
        let tag = "v0.7.0.6";
        let assets = json!([{"name": "tpf2mp-linux-0.7.0.6-native.tar.gz", "size": 10, "digest": format!("sha256:{}", "b".repeat(64)),
            "browser_download_url": format!("https://github.com/{PACKAGES_REPOSITORY}/releases/download/{tag}/tpf2mp-linux-0.7.0.6-native.tar.gz")}]);
        let joined = with_packages(bare.clone(), Some(assets.clone()));
        assert_eq!(packages(&joined, Mode::Native, false).unwrap()[0].url,
                   format!("https://github.com/{PACKAGES_REPOSITORY}/releases/download/{tag}/tpf2mp-linux-0.7.0.6-native.tar.gz"));
        // a packages-repo URL on a release whose assets came from the mod repo is refused, and the reverse
        let mut wrong = bare.clone(); wrong["assets"] = assets;
        assert!(packages(&wrong, Mode::Native, false).is_err());
        let old = release("v0.7.0.5", "0.7.0.5", false, &["tpf2mp-linux-0.7.0.5-native.tar.gz"]);
        assert!(packages(&with_packages(old.clone(), None), Mode::Native, false).is_ok());
        let mut moved = with_packages(old, None); moved["_assetsRepo"] = json!(PACKAGES_REPOSITORY);
        assert!(packages(&moved, Mode::Native, false).is_err());
    }
    #[test] fn newest_of_the_track() {
        let mut a = release("v0.7.0.4", "0.7.0.4", false, &[]); a["published_at"] = json!("2026-09-26T18:00:00Z");
        let b = release("v0.7.0.5", "0.7.0.5", false, &[]);
        let c = release("v0.7.1", "0.7.1 beta", false, &[]);
        assert_eq!(select_release(&[a.clone(), b.clone(), c.clone()], false).unwrap()["tag_name"], "v0.7.0.5");
        assert_eq!(select_release(&[a.clone(), b.clone(), c.clone()], true).unwrap()["tag_name"], "v0.7.1");
        // a launcher release published later is never the newest mod release, on either track
        let mut launcher = release("launcher-v1.2.0", "Launcher 1.2.0", false, &[]); launcher["published_at"] = json!("2026-09-27T00:00:00Z");
        let mut launcher_beta = release("launcher-v1.3.0", "Launcher 1.3.0 beta", true, &[]); launcher_beta["published_at"] = json!("2026-09-27T01:00:00Z");
        assert_eq!(select_release(&[a.clone(), b.clone(), launcher.clone()], false).unwrap()["tag_name"], "v0.7.0.5");
        assert_eq!(select_release(&[c.clone(), launcher_beta.clone()], true).unwrap()["tag_name"], "v0.7.1");
        assert!(!is_mod_release(&launcher) && !is_mod_release(&launcher_beta) && is_mod_release(&b));
    }
    #[test] fn process_scan_runs() { let _ = game_running(); }
    #[test] fn start_script_mark() {
        let dir = std::env::temp_dir().join(format!("tpf2-runsh-{}", std::process::id()));
        fs::create_dir_all(&dir).unwrap();
        let game = Game { folder: dir.clone(), kind: SteamKind::Native, mode: Mode::Native, steam_mode: Mode::Native };
        assert!(!start_script_ok(&game));                                   // no run.sh
        fs::write(dir.join("run.sh"), "#!/bin/sh\nexport LD_LIBRARY_PATH=.\n./TransportFever2 \"$@\"\n").unwrap();
        assert!(!start_script_ok(&game));                                   // Steam's own run.sh
        fs::write(dir.join("run.sh"), format!("#!/bin/sh\n{RUNSH_MARK}\nexport LD_PRELOAD=x\n# end of the tpf2mp block\n./TransportFever2\n")).unwrap();
        assert!(start_script_ok(&game));                                    // patched
        fs::write(dir.join("run.sh"), format!("#!/bin/sh\n  {RUNSH_MARK}\n")).unwrap();
        assert!(!start_script_ok(&game));                                   // only the exact line
        let _ = fs::remove_dir_all(dir);
    }
    #[test] fn switching_waits_for_steam() {
        let dir = std::env::temp_dir().join(format!("tpf2-mode-{}", std::process::id()));
        let game_dir = dir.join("steamapps/common/Transport Fever 2");
        fs::create_dir_all(&game_dir).unwrap();
        fs::write(game_dir.join("TransportFever2"), b"elf").unwrap();
        let mut settings = Settings::default();
        let g = make_game(game_dir.clone(), dir.clone(), &settings);
        assert_eq!((g.mode, g.steam_mode), (Mode::Native, Mode::Native));
        assert!(mode_note(&g).is_none());
        settings.mode_choice = Some(Mode::Proton);                  // chosen, Steam not switched yet
        let g = make_game(game_dir.clone(), dir.clone(), &settings);
        assert_eq!((g.mode, g.steam_mode), (Mode::Proton, Mode::Native));
        assert!(mode_note(&g).unwrap().contains("choose Proton"));
        // Steam switched (platform override) but has not downloaded the Windows build
        fs::create_dir_all(dir.join("steamapps")).unwrap();
        fs::write(dir.join(format!("steamapps/appmanifest_{APP_ID}.acf")),
                  "\"AppState\"\n{\n\t\"UserConfig\"\n\t{\n\t\t\"platform_override_source\"\t\t\"windows\"\n\t}\n}").unwrap();
        let g = make_game(game_dir.clone(), dir.clone(), &settings);
        assert_eq!(g.steam_mode, Mode::Proton);
        assert!(mode_note(&g).unwrap().contains("not finished downloading"));
        fs::write(game_dir.join("TransportFever2.exe"), b"pe").unwrap();
        assert!(mode_note(&make_game(game_dir.clone(), dir.clone(), &settings)).is_none());
        settings.mode_choice = Some(Mode::Native);                  // and back
        assert!(mode_note(&make_game(game_dir, dir.clone(), &settings)).unwrap().contains("untick"));
        let _ = fs::remove_dir_all(dir);
    }
    /// Against this machine's Steam and GitHub: `cargo test -- --ignored --nocapture`.
    #[test] #[ignore] fn live_environment() {
        let settings = load_settings();
        let game = find_game(&settings).expect("a Transport Fever 2 install");
        println!("game {:?} kind {:?} mode {:?} steam {:?}", game.folder, game.kind, game.mode, game.steam_mode);
        println!("installed {:?}", installed_version(&game));
        println!("start script patched {}", start_script_ok(&game));
        let latest = fetch(false).unwrap().expect("a stable release");
        println!("latest {}", summary(&latest, game.mode, false));
        for m in [Mode::Native, Mode::Proton] {
            println!("{:?}: {:?}", m, packages(&latest, m, false).map(|p| p.iter().map(|x| x.name.clone()).collect::<Vec<_>>()));
        }
        let old = fetch_version("0.6.1.19").unwrap();
        println!("0.6.1.19 native: {}", summary(&old, Mode::Native, false)["installationIssue"]);
    }
}
