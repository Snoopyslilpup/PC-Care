# Whatever IT PC Care

A trustworthy Windows diagnostic & safe-repair utility for home users — built so
you can sell it from your website. It checks a PC’s health, explains findings in
plain English, and offers **safe** fixes. It deliberately does **not** promise to
“magically” speed up a PC; it’s an honest diagnostic tool, with a clear path to
paid help from Whatever IT when a problem needs a technician.

> Built with C# / .NET 8 + WPF (a true native Windows app, not a browser window).
> Ships as a single self-contained `.exe` — **your customers do NOT need to
> install .NET**.

---

## Just install it (graphical, no command window)

**Double-click `Install.vbs`.** A proper setup window opens (wolf logo, progress
bar, status messages) and does everything behind it — **you never see a black
command/cmd window**. It will, with no manual steps:

1. Quietly find or download the components it needs the first time.
2. Build the app in the background (the progress bar keeps moving).
3. Install Whatever IT PC Care for your user account, add Desktop + Start-menu
   shortcuts, and launch it.

> The console work still happens — it is just hidden behind the setup window.
> The first run downloads ~200 MB of components and can take a few minutes; the
> window stays animated so you can see it is working, not frozen.

### Advanced / developer entry points (these DO show a console)

`setup.bat` is the build/automation tool for you as the developer. It runs in a
command window on purpose. Use `Install.vbs` above for the clean install
experience.


### All commands

```bat
setup.bat             :: install if needed, build first time, then LAUNCH
setup.bat rebuild     :: force a clean rebuild, then launch
setup.bat build       :: just produce publish\WhateverITPCCare.exe (no launch)
setup.bat installer   :: build + compile the Windows installer (needs Inno Setup)
setup.bat keygen "Name" BASIC               :: issue a lifetime key
setup.bat keygen "Name" PRO 2027-01-31      :: issue a subscription key
```

The single-file `publish\WhateverITPCCare.exe` is everything a customer needs.

---

## What it does

**Diagnostics (always safe — read-only):**
disk free space, drive SMART health, memory in use, temporary-file size,
Recycle Bin size, startup-program count, antivirus status, Windows Firewall,
pending restart, recent system errors, battery charge, and uptime — each with a
0–100 health score and a plain-English explanation.

**Free (diagnostics tier):**
full PC health scan, plus the launch-a-Windows-tool actions (open Disk Cleanup,
manage startup programs, open Windows Security, create a battery health report).

**Basic one-click repairs (R65/month):**
clear temporary files, empty Recycle Bin, refresh network (flush DNS) — the
“we do it for you” fixes.

**Advanced repairs (Pro – R99/month):**
System File Checker (SFC), DISM image repair, and fix-stuck-Windows-Update —
each elevates via the normal Windows UAC prompt only when run.

Anything riskier than this is intentionally left to a technician, with a
**Contact Whatever IT** button — that’s your paid-support funnel.

---

## Two revenue streams (built in, pay in-app)

* **Basic — R65 / month.** Unlocks the one-click safe repairs.
* **Pro — R99 / month.** Everything in Basic plus the advanced repairs.

Both are **monthly** and bought **inside the app**: the customer clicks **Get
Basic / Get Pro** on the **Plan & Billing** tab, the SimplyBlu payment page
opens in an embedded window, and the instant it confirms the payment the app
mints a **30-day** plan locally — no key to type, no email to wait for. When a
month lapses the plan pauses until they pay again (**Renew**).

The plan is signed with HMAC-SHA256 and bound to the customer's PC (machine +
user), so a copied `license.dat` fails on another machine. No activation server
to run. A manual **“Already have a licence key?”** box remains for support cases.

> **Testing right now:** both Basic and Pro currently point at the **same**
> SimplyBlu test checkout link so you can click through and watch the in-app
> auto-activation work end to end. Give each plan its own real link (in
> `src/MainWindow.xaml.cs`) before you sell.

---

## Testing the licensing engine

There is a tiny, zero-dependency test harness under `tests/LicenseTests` that
checks the whole licensing engine — key signing/validation, name binding,
tamper rejection, expiry, machine binding, purchase/activate/renew/deactivate
and the plan-unlock flags. It links the real app source (no copies) and needs
no NuGet restore, so it runs on a locked-down network.

```bat
cd tests\LicenseTests
run-tests.bat            :: or:  dotnet run -c Release
```

It prints `PASS` / `FAIL` per check and a final tally; exit code `0` means
everything passed. It temporarily backs up and restores your real
`license.dat`, so running it never disturbs an installed licence.

---

## Before you build for sale

* Open `src/Services/LicenseCore.cs` and change the `SECRET` phrase to your own
  long private phrase. This is the master key that signs every plan — keep it
  secret and never ship the keygen to customers.
* The SimplyBlu monthly payment links and prices live near the top of
  `src/MainWindow.xaml.cs` (`BasicPayUrl`, `ProPayUrl`, `BasicPrice`,
  `ProPrice`); website/contact details are there too (`WebsiteUrl`,
  `ContactPhone`).
* In your SimplyBlu dashboard, set each payment link's **success / return**
  behaviour to a page that shows wording like “Thank you for your payment” so the
  app can detect it. If SimplyBlu uses different wording, add it to
  `SuccessPhrases` in `src/CheckoutWindow.xaml.cs`.

### Get the installer with ZERO command line (recommended – cloud build)

If you never want to see a command window at all, let GitHub build the installer
for you on Microsoft's own Windows servers and just **download the finished
`.exe`**. A ready-made workflow is included at
`.github/workflows/build-installer.yml`.

One-time, entirely in your browser:

1. Create a free GitHub account and a new repository.
2. Upload this project to it (GitHub's web uploader accepts drag-and-drop; or
   use GitHub Desktop — no command line either way).
3. Open the repo's **Actions** tab → **Build installer** → **Run workflow**.
4. Wait ~2–3 minutes. When it turns green, download
   **`WhateverITPCCare-Setup.exe`** from the run's **Artifacts** section (or
   from the **Releases** page if you pushed a tag like `v1.0.0`).

That downloaded `WhateverITPCCare-Setup.exe` is the graphical wizard. It is the
only file you upload to your website / send to customers, and **no command
window is ever involved** — not for you, not for them. The slow “downloading
.NET / extracting the archive” step that can freeze on a home PC does not happen
here, because the cloud runner has no antivirus throttling the extraction.

### Or build the installer locally (one command window, on your PC)

Customers must get a **real Windows install wizard**, not the black `setup.bat`
command window. `setup.bat` is a **developer/build tool for your machine only** —
never send it, the source zip, or the `.dotnet` folder to a customer. If a
customer sees a command prompt (or it "gets stuck installing" while downloading
.NET), it means they were given `setup.bat` instead of the compiled installer.

To build the installer **once, on your own Windows PC** (this is the path that
shows one command window — use the cloud build above if you want none):

1. Unzip the source anywhere.
2. Run **`setup.bat installer`** (double-click, or type it in a command prompt
   opened in the folder).
3. A command window does the one-time work for you — it auto-installs the .NET
   build tools and the free **Inno Setup** compiler if they are missing (no
   admin, downloaded into the folder), compiles the app, builds the installer,
   and then **automatically opens the finished wizard** so you can see it.
4. The file it produced is **`Output\WhateverITPCCare-Setup.exe`** — this is the
   ONLY file you upload to your website and give to customers.

You no longer need to install Inno Setup by hand — the script does it. (The one
command window you see belongs to this build step on *your* PC; customers who
run `WhateverITPCCare-Setup.exe` never see it.)

> **If the window seems stuck on `Extracting the archive`:** it is almost
> certainly **not** frozen. That is the ~285 MB .NET SDK unpacking ~20,000
> files while your antivirus scans each one, which can take 10–30 minutes on a
> home PC. The script now adds a Microsoft Defender exclusion automatically when
> run as Administrator to speed this up, but on a slow disk it can still take a
> while — let it finish, or use the cloud build above to avoid it entirely.

What the customer experiences from that one `.exe`:

* a branded graphical wizard (wolf logo, welcome page, licence agreement they
  must accept, install location, progress bar, finish page) — **no command
  window at any point**;
* **no .NET download and no admin-only build** — the app is a self-contained
  single `.exe`, so there is nothing to compile on their machine;
* a desktop shortcut (opt-in checkbox) and Start-menu entries with the wolf
  icon, plus a proper entry in **Settings → Apps** for clean uninstall.

The wizard graphics live in `installer\assets\` (`wizard-large.bmp` for the
welcome/finish banner, `wizard-small.bmp` for the header). Swap those bitmaps if
you want different artwork — the sizes are 164×314 and 55×58 (with `@2x`
high-DPI versions).

### Issue a licence manually (support / offline only)

Customers normally never need this — they just pay in-app. For a support case
you can still mint a key:

```bat
setup.bat keygen "Jane Buyer" BASIC 2027-01-31   :: Basic key until that date
setup.bat keygen "Jane Buyer" PRO 2027-01-31      :: Pro key until that date
```

Send the customer **both** the exact name and the key. In the app they open the
**Plan & Billing** tab, expand **“Already have a licence key?”**, type the same
name + key, and click **Activate**.

---

## Honesty / notes

* This project was prepared in a Linux environment with **no .NET toolchain**, so
  the code is **static-checked only**. The first `setup.bat` run on your Windows
  PC does the real build — do a quick smoke test there. A native Windows app
  cannot be compiled on Linux.
* All repairs are conservative (Windows recreates temp files; cleanup never
  touches your documents). The advanced repairs are the standard
  Microsoft-recommended commands (SFC / DISM / Windows Update reset).
* Keep `src/Services/LicenseCore.cs` and the `keygen` tool private — they are how
  you mint keys. Never commit the real `SECRET` to a public repository.

## License / EULA

See `EULA.txt` for a starter end-user licence agreement you can adapt (template,
not legal advice — have it reviewed for your jurisdiction, e.g. South Africa /
POPIA).
