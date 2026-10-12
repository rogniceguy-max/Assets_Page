const { invoke } = window.__TAURI__.core;
const card = document.querySelector("#file-card");
const status = document.querySelector("#status");
const pathLabel = document.querySelector("#path");
let pointerStart = null;
let nativeDragRequested = false;

function setStatus(message, kind = "") {
  status.textContent = message;
  status.dataset.kind = kind;
}

async function showPath() {
  try {
    pathLabel.textContent = await invoke("synthetic_file_path");
  } catch (error) {
    pathLabel.textContent = String(error);
    setStatus("Could not resolve the synthetic file path.", "error");
  }
}

card.addEventListener("pointerdown", (event) => {
  if (event.button !== 0) return;
  pointerStart = { x: event.clientX, y: event.clientY, id: event.pointerId };
  nativeDragRequested = false;
});

card.addEventListener("pointermove", async (event) => {
  if (!pointerStart || nativeDragRequested || event.pointerId !== pointerStart.id || !(event.buttons & 1)) return;
  const movedX = Math.abs(event.clientX - pointerStart.x);
  const movedY = Math.abs(event.clientY - pointerStart.y);
  if (movedX < 8 && movedY < 8) return;

  nativeDragRequested = true;
  pointerStart = null;
  setStatus("Starting native copy-only drag…");
  try {
    const result = await invoke("start_native_file_drag");
    setStatus(result || "Drag operation returned.", "success");
  } catch (error) {
    setStatus(`Native drag failed: ${String(error)}`, "error");
  }
});

card.addEventListener("pointerup", () => { pointerStart = null; });
card.addEventListener("pointercancel", () => { pointerStart = null; });

document.querySelector("#open-receiver").addEventListener("click", async () => {
  try {
    await invoke("open_local_receiver");
    setStatus("Separate WPF receiver opened. Drag the synthetic file card into it.", "success");
  } catch (error) {
    setStatus(`Could not open receiver: ${String(error)}`, "error");
  }
});

showPath();
