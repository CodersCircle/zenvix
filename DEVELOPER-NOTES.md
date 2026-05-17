# 📘 ZENVIX Developer Cheat-Sheet & Architecture Notes

Welcome to the official developer notes for **ZENVIX**! This file serves as a quick reference guide for git operations, codebase structure, and local developer setups.

---

## 🚀 1. Git Workflow (How to Push Code)

Follow these steps in your terminal (PowerShell, Command Prompt, or Git Bash) from the project root (`Hostix/`) to stage, commit, and push your changes:

### 📝 Standard Command Sequence
```bash
# 1. View your modified files
git status

# 2. Stage all changed files under the docs/ folder
git add docs/

# 3. Create a commit with a descriptive message
git commit -m "style: optimized spacing and layout alignment"

# 4. Upload your changes to the development branch on GitHub
git push origin development
```

### 💡 Extra Git Helpers
* **Check detailed line changes before staging:**
  ```bash
  git diff docs/
  ```
* **Grab the latest remote changes from GitHub:**
  ```bash
  git pull origin development
  ```

---

## 📁 2. Modular File Architecture

The website has been refactored from a single huge index file into a clean, modern modular folder system under the `docs/` directory:

```text
docs/
├── index.html                  # Core visual layout, SEO tags, and navigation anchors (No inline scripts!)
├── style.css                   # Custom global CSS styles, animations, and responsive media queries
├── assets/                     # Shared static media folder
│   ├── Icons/
│   │   └── favicon.ico         # Custom browser head icon
│   └── logo/
│       └── zen-logo.png        # Official ZENVIX brand logo
├── js/
│   └── app.js                  # Central JavaScript orchestrator (Toggles, spinner, toast, fetched modal logs)
├── components/
│   └── dashboard-preview.html  # Dynamic high-fidelity Material 3 developer dashboard template
└── legal/
    ├── privacy.html            # Standalone Privacy Policy document
    ├── terms.html              # Standalone Terms of Service document
    └── agreement.html          # Standalone User Agreement document
```

---

## ⚡ 3. Key Design Features & Dynamic Logs

### 🔴 glowing Windows Warning (Mobile-Only)
* **HTML Element Class:** `.os-warning`
* **Visibility:** Dynamically hidden on desktop and **only visible on mobile screens** (`max-width: 768px`) directly below the three download setup buttons.
* **Effect:** Features a custom pulse-red glowing text shadow heartbeat animation.

### 🇮🇳 Bouncing Flag Dots Copyright Line
* **Spelling & Syntax:** Nesting `.india-dot saffron/white/green` inside a single inline `<span class="india-flag-dots-inline">` inside `.footer-copyright`.
* **Benefit:** Ensures that the flag dots wrap beautifully along with the text word-by-word on mobile viewports rather than clipping.

### 🔢 Simulated Download Counter
* **Baseline Value:** Hardcoded to **437 Downloads** in both `docs/index.html` and `docs/js/app.js` (`let baseDownloads = 437`).
* **Real-time Tracker:** Increments dynamically in the browser cache (`localStorage`) when the user clicks any download button and synchronizes with GitHub Release API values!

---

## ⚠️ 4. CORS fetch Safeguards (Local Testing)
Because the legal documents and dashboard previews are decoupled as separate static HTML pages, the JavaScript uses the asynchronous `fetch()` API to fetch them dynamically into the modals. 
* **Server required:** To view modal popups locally on your machine, run a local web server (e.g. VS Code **Live Server** extension, or run `python -m http.server` in the `docs` folder).
* **Production ready:** The fetches are relative, so they operate perfectly once deployed onto GitHub Pages (`https://CodersCircle.github.io/zenvix/`).
