const form = document.querySelector("#login-form");
const password = document.querySelector("#password");
const status = document.querySelector("#status");
const submitButton = document.querySelector("#submit-button");

function showError(message) {
    status.textContent = message;
    status.hidden = false;
}

async function readError(response) {
    try {
        const problem = await response.json();
        return problem.title || problem.detail || `Sign-in failed (${response.status}).`;
    } catch {
        return `Sign-in failed (${response.status}).`;
    }
}

form.addEventListener("submit", async event => {
    event.preventDefault();
    if (!password.value) {
        return;
    }
    submitButton.disabled = true;
    submitButton.textContent = "Signing in...";
    status.hidden = true;

    try {
        const response = await fetch("/login", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ password: password.value })
        });
        if (!response.ok) {
            throw new Error(await readError(response));
        }
        window.location.assign("/");
    } catch (error) {
        showError(error.message || "The password was not accepted.");
        password.select();
    } finally {
        submitButton.disabled = false;
        submitButton.textContent = "Continue";
    }
});
