const form = document.querySelector("#shorten-form");
const urlInput = document.querySelector("#url-input");
const shortUrlInput = document.querySelector("#short-url");
const copyButton = document.querySelector("#copy-button");
const statusText = document.querySelector("#status");

function setStatus(message, isError = false) {
  statusText.textContent = message;
  statusText.classList.toggle("error", isError);
}

form.addEventListener("submit", async (event) => {
  event.preventDefault();
  setStatus("Shortening...");
  shortUrlInput.value = "";
  copyButton.disabled = true;

  try {
    const response = await fetch("/api/links", {
      method: "POST",
      headers: {
        "Content-Type": "application/json"
      },
      body: JSON.stringify({ url: urlInput.value })
    });

    const payload = await response.json().catch(() => ({}));

    if (!response.ok) {
      throw new Error(payload.error || "Could not shorten that URL.");
    }

    shortUrlInput.value = payload.shortUrl;
    copyButton.disabled = false;
    setStatus("Ready.");
  } catch (error) {
    setStatus(error.message, true);
  }
});

copyButton.addEventListener("click", async () => {
  if (!shortUrlInput.value) {
    return;
  }

  await navigator.clipboard.writeText(shortUrlInput.value);
  setStatus("Copied.");
});
