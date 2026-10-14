# Build the Installer in the Cloud

This folder holds the automated “recipe” (`build-installer.yml`) that builds
**Whatever IT PC Care** for you on Microsoft’s own Windows servers and hands
you back a ready-to-share installer: **`WhateverITPCCare-Setup.exe`**.

You do **not** need Visual Studio, the .NET SDK, Inno Setup, or any command
window on your own PC. Everything happens in the browser.

---

## What the workflow does for you

Every time it runs, it automatically:

1. Downloads your project from the repository.
2. Installs the **.NET 8 SDK** on a fresh Windows machine.
3. Builds the app as a **self-contained** program (your customers do **not**
   need to install .NET first).
4. Installs **Inno Setup** and compiles your branded setup wizard.
5. Publishes **`WhateverITPCCare-Setup.exe`** as a file you can download.

---

## How to run it (click-by-click, all in the browser)

1. Open your repository on GitHub.
2. Click the **Actions** tab at the top.
3. In the left-hand list, click **Build installer**.
4. On the right, click the **Run workflow** button, then the green
   **Run workflow** button in the little drop-down.
5. Wait about **2–3 minutes**. A yellow dot means it’s working; a green
   check means it finished.

---

## Where to get the finished installer

After the run shows a green check mark:

1. Click on the finished run (the one at the top of the list).
2. Scroll down to the **Artifacts** section.
3. Download **`WhateverITPCCare-Setup`** – it unzips to
   `WhateverITPCCare-Setup.exe`.

That `.exe` is the graphical install wizard you give to customers. Nobody
ever sees a command window.

---

## Making an official Release (optional)

If you create a version **tag** that starts with `v` (for example `v1.0.0`),
the workflow does everything above **and** attaches the installer to a
GitHub **Release** automatically, so there’s a clean public download page.

To do this in the browser:

1. Go to the **Releases** section of your repo (right-hand side of the main
   page) and click **Draft a new release** (or **Create a new release**).
2. Under **Choose a tag**, type a new tag such as `v1.0.0` and choose
   **Create new tag on publish**.
3. Give it a title (e.g. *Whatever IT PC Care 1.0.0*) and click
   **Publish release**.
4. The **Build installer** workflow starts on its own; when it finishes, the
   `WhateverITPCCare-Setup.exe` appears as a download on that Release page.

> Keep the tag number in step 2 in step with `AppVersion` in `installer.iss`
> (currently `1.0.0`) so the download name and the installer match.

---

## When does it run?

| Trigger | What you do | Result |
|---|---|---|
| **Manual** | Actions tab → Run workflow | Installer in **Artifacts** |
| **Version tag** (`v*`) | Publish a release tagged `v1.0.0` | Installer in **Artifacts** *and* on the **Release** page |

---

## If a build fails (red ✖)

1. Click the failed run, then click the **build** job.
2. Open the step with the red ✖ and read the last few lines – they say what
   went wrong (usually a typo in a code or `.iss` file).
3. Fix the file in the browser (pencil / **Edit** icon → **Commit changes**).
4. Run the workflow again from the **Actions** tab.

If the message isn’t clear, copy those last few lines and send them over –
they’re enough to pinpoint the fix.

---

## Quick reference

- **Recipe file:** `.github/workflows/build-installer.yml`
- **App project built:** `src/WhateverITPCCare.csproj`
- **Installer script:** `installer.iss`
- **Final file:** `WhateverITPCCare-Setup.exe` (from the `Output` folder)
- **Build machine:** `windows-latest` (a clean Windows server, every time)
- **Typical time:** ~2–3 minutes
