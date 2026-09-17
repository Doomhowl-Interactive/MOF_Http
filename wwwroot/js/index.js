const form = document.querySelector("#unwrap-form");
const dropZone = document.querySelector("#drop-zone");
const fileInput = document.querySelector("#file-input");
const fileDetails = document.querySelector("#file-details");
const status = document.querySelector("#status");
const submitButton = document.querySelector("#submit-button");
let selectedFile = null;

function showStatus(message, kind) {
    status.textContent = message;
    status.className = `alert alert-${kind} mb-3`;
    status.hidden = false;
}

function selectFile(file) {
    if (!file || file.size === 0 || !file.name.toLowerCase().endsWith(".obj")) {
        selectedFile = null;
        fileDetails.hidden = true;
        submitButton.disabled = true;
        showStatus("Choose a nonempty .obj file.", "danger");
        return;
    }

    selectedFile = file;
    fileDetails.textContent = `${file.name} (${formatBytes(file.size)})`;
    fileDetails.hidden = false;
    submitButton.disabled = false;
    status.hidden = true;
}

function formatBytes(bytes) {
    if (bytes < 1024) {
        return `${bytes} bytes`;
    }

    if (bytes < 1024 * 1024) {
        return `${(bytes / 1024).toFixed(1)} KB`;
    }

    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

async function readError(response) {
    try {
        const problem = await response.json();
        if (problem.errors) {
            return Object.values(problem.errors).flat().join(" ");
        }
        return problem.detail || problem.title || `Request failed (${response.status}).`;
    } catch {
        return `Request failed (${response.status}).`;
    }
}

fileInput.addEventListener("change", () => selectFile(fileInput.files[0]));

for (const eventName of ["dragenter", "dragover"]) {
    dropZone.addEventListener(eventName, event => {
        event.preventDefault();
        dropZone.classList.remove("border-secondary");
        dropZone.classList.add("border-info", "bg-secondary");
    });
}

for (const eventName of ["dragleave", "drop"]) {
    dropZone.addEventListener(eventName, event => {
        event.preventDefault();
        dropZone.classList.remove("border-info", "bg-secondary");
        dropZone.classList.add("border-secondary");
    });
}

dropZone.addEventListener("drop", event => selectFile(event.dataTransfer.files[0]));
dropZone.addEventListener("keydown", event => {
    if (event.key === "Enter" || event.key === " ") {
        event.preventDefault();
        fileInput.click();
    }
});

form.addEventListener("submit", async event => {
    event.preventDefault();
    if (!selectedFile) {
        return;
    }

    const data = new FormData();
    data.append("File", selectedFile, selectedFile.name);
    submitButton.disabled = true;
    submitButton.textContent = "Unwrapping...";
    showStatus("Uploading and generating UV coordinates. This may take a moment.", "info");

    try {
        const response = await fetch("/api/unwrap", { method: "POST", body: data });
        if (!response.ok) {
            throw new Error(await readError(response));
        }

        const output = await response.blob();
        const url = URL.createObjectURL(output);
        const download = document.createElement("a");
        const baseName = selectedFile.name.replace(/\.obj$/i, "");
        download.href = url;
        download.download = `${baseName}-unwrapped.obj`;
        download.click();
        URL.revokeObjectURL(url);
        showStatus("Unwrap complete. Your OBJ download has started.", "success");
    } catch (error) {
        showStatus(error.message || "The mesh could not be unwrapped.", "danger");
    } finally {
        submitButton.disabled = false;
        submitButton.textContent = "Unwrap mesh";
    }
});
