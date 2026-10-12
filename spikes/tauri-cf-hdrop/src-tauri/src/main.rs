#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use drag::{DragItem, DragMode, Image, Options};
use std::path::PathBuf;
use std::process::Command;
use tauri::{Manager, WebviewWindow};

fn synthetic_file() -> Result<PathBuf, String> {
    let path = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("synthetic-test-file.txt");
    std::fs::canonicalize(&path).map_err(|error| format!("Synthetic file is unavailable: {error}"))
}

#[tauri::command]
fn synthetic_file_path() -> Result<String, String> {
    synthetic_file().map(|path| path.to_string_lossy().into_owned())
}

#[tauri::command]
fn open_local_receiver() -> Result<(), String> {
    let receiver = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join(
        "../../wpf-cf-hdrop/bin/Release/net8.0-windows/WpfCfHdropSpike.exe",
    );
    if !receiver.is_file() {
        return Err(format!("Build the WPF spike receiver first: {}", receiver.display()));
    }
    Command::new(&receiver)
        .arg("--receiver")
        .spawn()
        .map(|_| ())
        .map_err(|error| format!("Could not start the local receiver: {error}"))
}

#[tauri::command]
fn start_native_file_drag(window: WebviewWindow) -> Result<String, String> {
    let path = synthetic_file()?;
    let drag_window = window.clone();
    window
        .run_on_main_thread(move || {
            let result = drag::start_drag(
                &drag_window,
                DragItem::Files(vec![path]),
                Image::Raw(Vec::new()),
                |outcome, _cursor| eprintln!("Native file drag finished: {outcome:?}"),
                Options {
                    mode: DragMode::Copy,
                    ..Options::default()
                },
            );
            if let Err(error) = result {
                eprintln!("Native file drag failed: {error}");
            }
        })
        .map_err(|error| format!("Could not schedule the native drag: {error}"))?;

    Ok("Native copy-only drag queued. Drop the file on the separate WPF receiver.".into())
}

fn main() {
    tauri::Builder::default()
        .invoke_handler(tauri::generate_handler![
            synthetic_file_path,
            open_local_receiver,
            start_native_file_drag
        ])
        .run(tauri::generate_context!())
        .expect("error while running Tauri CF_HDROP spike");
}
