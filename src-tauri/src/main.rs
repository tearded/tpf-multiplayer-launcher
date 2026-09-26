#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

#[cfg(windows)]
use std::io::{BufRead, BufReader, Read};
#[cfg(windows)]
use std::os::windows::process::CommandExt;
#[cfg(windows)]
use std::process::{Child, ChildStdout, Command, ExitStatus, Stdio};
#[cfg(windows)]
use std::sync::{Mutex, mpsc};
use std::sync::{Arc, atomic::{AtomicBool, Ordering}};
use std::time::Duration;
use tauri::{Emitter, Manager};

// Linux has no C# helper: the same actions, answered in Rust (see linux.rs).
#[cfg(target_os = "linux")]
mod linux;

#[derive(Clone, Default)]
struct Operation(Arc<AtomicBool>);
struct Reset(Arc<AtomicBool>);
impl Drop for Reset { fn drop(&mut self) { self.0.store(false, Ordering::SeqCst); } }

fn allowed(action: &str) -> bool {
    matches!(action, "status" | "fetch" | "install" | "uninstall" | "recover" | "play" | "game-folder" | "choose-folder" | "backups" | "release-page" | "release-link" | "backup-limit" | "history" | "release-track" | "game-mode")
}

fn valid_release_link(value: &str) -> bool {
    if value.len() > 2048 { return false; }
    tauri::Url::parse(value).is_ok_and(|url| url.scheme() == "https" && url.host_str() == Some("github.com") && url.username().is_empty() && url.password().is_none() && url.port().is_none() && url.path().starts_with("/silver2127/tpf2-multiplayer/"))
}

// Installations wait on downloads, UAC prompts and msiexec; the folder dialog waits on the user.
// Everything else finishes quickly, so a stuck helper must not hold the operation lock forever.
fn time_limit(action: &str) -> Option<Duration> {
    match action {
        "install" | "uninstall" | "recover" | "choose-folder" => None,
        "fetch" | "history" => Some(Duration::from_secs(600)),
        _ => Some(Duration::from_secs(60)),
    }
}

#[cfg(windows)]
fn read_results(app: &tauri::AppHandle, stdout: ChildStdout) -> Result<Option<serde_json::Value>, String> {
    let mut result = None;
    for line in BufReader::new(stdout).lines() {
        let line = line.map_err(|e|e.to_string())?;
        let value: serde_json::Value = serde_json::from_str(&line).map_err(|_|"Invalid response from the launcher helper")?;
        if value["kind"] == "progress" { let _ = app.emit("native-progress", &value); }
        else { result = Some(value); }
    }
    Ok(result)
}

// Poll instead of blocking inside the lock, so the watchdog can still kill the helper.
#[cfg(windows)]
fn wait_for(child: &Mutex<Child>) -> Result<ExitStatus, String> {
    loop {
        if let Some(status) = child.lock().map_err(|_|"The launcher helper failed.")?.try_wait().map_err(|e|e.to_string())? { return Ok(status); }
        std::thread::sleep(Duration::from_millis(20));
    }
}

// Cheap process check for status polling; the full helper status also hashes game files.
#[cfg(target_os = "linux")]
#[tauri::command]
fn game_running() -> Result<bool, String> { Ok(linux::game_running()) }

#[cfg(windows)]
#[tauri::command]
fn game_running() -> Result<bool, String> {
    use windows_sys::Win32::Foundation::{CloseHandle, INVALID_HANDLE_VALUE};
    use windows_sys::Win32::System::Diagnostics::ToolHelp::{CreateToolhelp32Snapshot, Process32FirstW, Process32NextW, PROCESSENTRY32W, TH32CS_SNAPPROCESS};
    unsafe {
        let snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if snapshot == INVALID_HANDLE_VALUE { return Err("Could not read the process list.".into()); }
        let mut entry: PROCESSENTRY32W = std::mem::zeroed();
        entry.dwSize = std::mem::size_of::<PROCESSENTRY32W>() as u32;
        let mut running = false;
        let mut more = Process32FirstW(snapshot, &mut entry) != 0;
        while more {
            let length = entry.szExeFile.iter().position(|&c| c == 0).unwrap_or(entry.szExeFile.len());
            if String::from_utf16_lossy(&entry.szExeFile[..length]).eq_ignore_ascii_case("TransportFever2.exe") { running = true; break; }
            more = Process32NextW(snapshot, &mut entry) != 0;
        }
        CloseHandle(snapshot);
        Ok(running)
    }
}

// `value` is the action's single argument: a release version, history page, backup limit,
// release track (0/1) or, for release-link, a GitHub URL.
#[tauri::command]
async fn native_action(app: tauri::AppHandle, state: tauri::State<'_, Operation>, action: String, value: Option<String>) -> Result<serde_json::Value, String> {
    if !allowed(&action) { return Err("Unknown launcher action.".into()); }
    if action == "release-link" {
        if !value.as_deref().is_some_and(valid_release_link) { return Err("Invalid release link.".into()); }
    } else if let Some(ref value) = value { if value.len()>32 || !value.chars().all(|c| c.is_ascii_digit() || c == '.') { return Err("Invalid launcher argument.".into()); } }
    if state.0.swap(true, Ordering::SeqCst) { return Err("An operation is already in progress. Please wait.".into()); }
    let guard = Reset(state.0.clone());
    #[cfg(target_os = "linux")]
    {
        let _ = time_limit;
        return tauri::async_runtime::spawn_blocking(move || {
            let _guard = guard;
            linux::run(&app, &action, value.as_deref())
        }).await.map_err(|e|e.to_string())?;
    }
    #[cfg(windows)]
    {
    let helper = app.path().resource_dir().map_err(|e|e.to_string())?.join("TPF2Launcher.Native.exe");
    tauri::async_runtime::spawn_blocking(move || {
        let _guard = guard;
        let mut command = Command::new(helper);
        command.arg(&action);
        if let Some(value) = value { command.arg(value); }
        let mut child = command.creation_flags(0x08000000).stdin(Stdio::null()).stdout(Stdio::piped()).stderr(Stdio::piped()).spawn().map_err(|e|format!("Could not start the launcher helper: {e}"))?;
        let stdout = child.stdout.take().ok_or("The launcher helper has no output")?;
        let mut stderr = child.stderr.take().ok_or("The launcher helper has no output")?;
        let errors = std::thread::spawn(move || { let mut text = String::new(); let _ = stderr.read_to_string(&mut text); text });
        let child = Arc::new(Mutex::new(child));
        let timed_out = Arc::new(AtomicBool::new(false));
        let (done, finished) = mpsc::channel::<()>();
        if let Some(limit) = time_limit(&action) {
            let (child, timed_out) = (child.clone(), timed_out.clone());
            std::thread::spawn(move || {
                if finished.recv_timeout(limit) == Err(mpsc::RecvTimeoutError::Timeout) {
                    timed_out.store(true, Ordering::SeqCst);
                    if let Ok(mut child) = child.lock() { let _ = child.kill(); }
                }
            });
        }
        let output = read_results(&app, stdout);
        if output.is_err() { if let Ok(mut child) = child.lock() { let _ = child.kill(); } }
        let status = wait_for(&child);
        drop(done);
        let errors = errors.join().unwrap_or_default();
        if timed_out.load(Ordering::SeqCst) { return Err("The launcher helper stopped responding and was closed. Please try again.".into()); }
        let status = status?;
        let Some(value) = output? else {
            // The helper reports its own errors as JSON; stderr only matters when it could not run at all.
            let detail: String = errors.lines().map(str::trim).find(|line| !line.is_empty()).unwrap_or("").chars().take(300).collect();
            return Err(if detail.is_empty() { "The launcher helper exited without a result.".into() } else { format!("The launcher helper exited without a result: {detail}") });
        };
        if !status.success() || value["ok"] != true { return Err(value["error"].as_str().unwrap_or("Operation failed.").to_owned()); }
        Ok(value["data"].clone())
    }).await.map_err(|e|e.to_string())?
    }
}

fn main() {
    let builder = tauri::Builder::default();
    // the folder dialog of "choose-folder" (Windows uses the helper's own)
    #[cfg(target_os = "linux")]
    let builder = builder.plugin(tauri_plugin_dialog::init());
    builder
        .plugin(tauri_plugin_single_instance::init(|app, _, _| { if let Some(window)=app.get_webview_window("main") { let _=window.unminimize(); let _=window.set_focus(); } }))
        .plugin(tauri_plugin_updater::Builder::new().build())
        .manage(Operation::default())
        .invoke_handler(tauri::generate_handler![native_action, game_running])
        .on_window_event(|window, event| {
            if let tauri::WindowEvent::CloseRequested { api, .. } = event {
                if window.state::<Operation>().0.load(Ordering::SeqCst) {
                    api.prevent_close(); let _=window.emit("operation-busy", ());
                }
            }
        })
        .run(tauri::generate_context!())
        .expect("Could not start TPF2 Launcher");
}

#[cfg(test)]
mod tests {
    #[test] fn release_links_stay_in_upstream_repository() {
        assert!(super::valid_release_link("https://github.com/silver2127/tpf2-multiplayer/releases/tag/v0.6.1.18"));
        for value in ["file:///C:/Windows/test.exe", "https://github.com/other/repository/", "https://github.com@evil.com/silver2127/tpf2-multiplayer/", "https://github.com/silver2127/tpf2-multiplayer/../../../other", "https://github.com/silver2127/tpf2-multiplayer-evil/"] { assert!(!super::valid_release_link(value)); }
    }
    #[test] fn only_quick_operations_are_time_limited() {
        for action in ["install", "uninstall", "recover", "choose-folder"] { assert!(super::time_limit(action).is_none()); }
        for action in ["status", "fetch", "play"] { assert!(super::time_limit(action).is_some()); }
    }
    #[test] fn process_list_is_readable() { assert!(super::game_running().is_ok()); }
    #[test] fn only_fixed_operations() { assert!(super::allowed("install")); assert!(!super::allowed("cmd")); assert!(!super::allowed("../program.exe")); }
}
