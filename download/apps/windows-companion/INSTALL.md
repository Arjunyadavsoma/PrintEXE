# Installing PrintShop Companion (for shopkeepers)

These instructions are for a **non-technical print shop owner**. You do not need to install Node.js, Python, or any developer tool. You only need a Windows 10 or Windows 11 PC and an internet connection.

## What you need

- A Windows 10 or Windows 11 PC (64-bit).
- The PC must be the same one connected to the printer(s) you want to use.
- Your PrintShop website login.
- Internet connection.

## Step 1 — Get the installer

You have two options:

### Option A — Download the finished installer (recommended)

If the PrintShop team has run the build, the installer file is available as a GitHub release. Download:

- `PrintShopCompanion.Setup.exe` (the recommended installer — it bundles the .NET runtime, so you don't need to install anything else), OR
- `PrintShopCompanion.msi` (a smaller installer that requires .NET 10 Desktop Runtime to already be installed).

Save it to your Downloads folder.

### Option B — Build the installer yourself (advanced)

You only need this if you're a developer working on the companion itself. Run the included `scripts/install-on-windows.ps1` script. It will install .NET 10 + WiX, build the project, and produce the same `.msi` and `.exe` files in your Downloads folder.

## Step 2 — Install PrintShop Companion

1. Find the `PrintShopCompanion.Setup.exe` file you downloaded (or `PrintShopCompanion.msi`).
2. **Double-click it.**
3. Click **Install** on the welcome screen.
4. Wait for the install to finish. This typically takes less than a minute.
5. Click **Finish**.

PrintShop Companion is now installed. You'll find it in your Start menu under **PrintShop Companion**.

## Step 3 — Open PrintShop Companion

Click the Start button, type "PrintShop Companion", and press Enter.

The first time you open it, you'll see the Setup screen.

## Step 4 — Pair this PC with your shop

1. Open PrintShop Companion.
2. On the **Setup** screen, click **Begin pairing**. The companion waits for the website.
3. Open your web browser and sign in to your PrintShop website.
4. Go to **Shop → Printers → Add this PC**.
5. Click the **Connect Computer** button.
6. Your browser will ask to open PrintShop Companion. Click **Open**.
7. The companion automatically completes pairing — **no code to type**.
8. You should now see your shop name on the companion's Setup screen.

## Step 5 — Pick a printer

1. In the companion, click **Printers** on the left.
2. The companion lists every printer that Windows knows about on this PC.
3. Click the printer you want to use.
4. Click **Set as default for this PC**.
5. Click **Test print** to make sure the printer works.
6. A test page should come out of your printer. If it does, your setup is complete.

## Step 6 — Enable automatic printing

1. In the companion, click **Settings** on the left.
2. Make sure **Run on Windows startup** is on (it should be on by default).
3. Make sure **Minimise to tray on close** is on (also default).
4. Close the companion window — it will keep running in the background, in your system tray.

That's it. Whenever a customer uploads a document and places a print order on your PrintShop website, the companion will automatically:

1. Receive the job.
2. Download the PDF.
3. Send it to your selected printer.
4. Report back to the website that the order is printed.

You'll see a small PrintShop icon in your system tray (next to the clock). Double-click it any time to see the Dashboard.

## If something goes wrong

| Symptom | What to do |
| --- | --- |
| The companion won't open | Check the Start menu for "PrintShop Companion". If it's not there, re-run the installer. |
| Pairing doesn't complete | Make sure you're signed in to the PrintShop website on the same computer. Try the "Connect Computer" button again. |
| Test print doesn't print | Check that the printer is on, has paper, and is connected. Try the test print again. If it still fails, check the Printers screen — the printer may show as **Offline**. |
| Jobs are not arriving | Check your internet connection. The companion will queue jobs locally and send them once you reconnect. |
| A job shows "Outcome unknown" | Open the Print Queue screen. The companion could not confirm whether the paper actually came out. Look at your printer — if the page printed, click the job and then **Mark as printed**. If it didn't print, click **Reprint**. |
| The companion keeps crashing | Open Settings → Open log folder. Email the contents of the most recent `companion-YYYYMMDD.log` file to PrintShop support. |

## How to uninstall

1. Open **Settings → Apps → Installed apps** (Windows 11) or **Settings → Apps → Apps & features** (Windows 10).
2. Find **PrintShop Companion**.
3. Click **Uninstall**.
4. Confirm. The companion is removed cleanly.

Your PrintShop account is not affected. Your paired device is automatically marked offline. You can re-pair a different PC any time from the website.
