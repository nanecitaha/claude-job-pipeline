"use strict";

const form = document.getElementById("loginForm");
const error = document.getElementById("loginError");

// Only go back to a page on this site.
function nextPage() {
  const next = new URLSearchParams(location.search).get("next") || "/";
  return next.startsWith("/") && !next.startsWith("//") && !next.startsWith("/\\") ? next : "/";
}

form.addEventListener("submit", async (e) => {
  e.preventDefault();
  const button = form.querySelector("button");
  const body = {
    username: form.elements.username.value.trim(),
    password: form.elements.password.value,
    remember: form.elements.remember.checked,
  };
  if (!body.username || !body.password) {
    error.textContent = "Enter your username and password.";
    error.hidden = false;
    return;
  }
  button.disabled = true;
  error.hidden = true;
  try {
    const res = await fetch("/api/login", {
      method: "POST",
      headers: { "Content-Type": "application/json", "X-Requested-With": "JobPipeline" },
      body: JSON.stringify(body),
    });
    if (res.ok) { location.replace(nextPage()); return; }
    const data = await res.json().catch(() => null);
    error.textContent = data?.error || `Sign-in failed (${res.status}).`;
    error.hidden = false;
    form.elements.password.select();
  } catch {
    error.textContent = "Can't reach the dashboard server.";
    error.hidden = false;
  } finally {
    button.disabled = false;
  }
});
