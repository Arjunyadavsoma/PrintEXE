# PrintShop Windows Setup — Shopkeeper Guide

A plain-language guide for shop owners setting up the PrintShop desktop app
(PrintEXE / "PrintShop Companion") on a Windows computer.

## What you need

- A Windows 10 or 11 PC (64-bit) that stays switched on during shop hours.
- A printer already installed in Windows (print a test page from Windows first).
- Your PrintShop website account (email + password).

## Step 1 — Install the app

1. Download `PrintEXE-v<version>-win-x64.zip` from the GitHub Releases page
   your PrintShop support person gives you.
2. Right-click the ZIP → *Extract all* → pick a folder such as `C:\PrintEXE`.
3. Double-click `PrintShopCompanion.exe` inside that folder.
4. If Windows SmartScreen warns you, click *More info* → *Run anyway*.

You do NOT need to install anything else — the app includes everything.

## Step 2 — Connect the app to your shop

**Easiest way (deep link):**
1. On the website, open **Shop → Devices → Add Windows Device**.
2. Click the **"Add this PC"** button on the website.
3. Your browser asks permission to open the PrintShop app — click *Open*.
4. The app's login screen now shows the pairing code pre-filled. Click
   **Pair this computer** to confirm. (The code is only valid for 5 minutes —
   if it expires, click *Add Windows Device* on the website again.)

**Alternative (manual code):** click *Add Windows Device* on the website,
copy the 64-character pairing code, paste it into the app's pairing box, and
click **Pair this computer**.

After pairing, the app shows your shop's name. Check it is YOUR shop before
continuing.

## Step 3 — Choose your printer

1. Open the **Printers** tab in the app.
2. Click your shop's printer in the list → *Select*.
3. The choice is saved automatically (it survives restarts) and appears on
   the website's Devices page within ~15 seconds.

## Step 4 — Check the connection

- The app's dashboard shows a green/connected indicator when paired.
- The website's **Devices** page shows the computer as **Online** with the
  printer name you selected.

## Everyday use

- Customers submit documents on your website; they appear in the app's queue
  and print automatically on your selected printer.
- Pause/Resume buttons stop and restart the queue without losing jobs.
- If a print result is uncertain (paper jam, spooler timeout), the job is
  marked **Needs review** in the app and on the website — check the printer,
  then mark the job *Printed* or *Failed* from the website's job screen
  (the website records who resolved it and when). Nothing reprints by itself.
- Internet down? The app keeps working locally and syncs when the internet
  returns; jobs never print twice.

## Keeping the app up to date

The app checks GitHub Releases on startup. When an update is available, a
banner appears — click **Update Now**. Verify the download with the listed
SHA-256 checksum if you want to be thorough.

## Troubleshooting

| Problem | What to do |
|---|---|
| Pairing code expired | Generate a new one on the website (valid 5 minutes). |
| "This device has been disabled" | You (or staff) disabled the PC on the website — re-enable it in Shop → Devices. |
| App shows offline on the website | Check the internet; the app retries automatically every 15 seconds. |
| Wrong printer printed | Change the printer in the app's Printers tab. |
| Job stuck on "Needs review" | Look at the printer, then resolve the job on the website. |
| Credential was rotated but the app lost it | Re-pair: Settings → *Unpair this PC*, then repeat Step 2. |
| App won't start after update | Re-extract the ZIP into the same folder and run again. |

## Security notes

- Your computer's credential is encrypted with Windows DPAPI — it only works
  on this PC, for your Windows user account.
- You can disable or completely remove a PC from **Shop → Devices** at any
  time; removal instantly blocks that computer.
- Never share pairing codes or device tokens — treat them like passwords.


---

## Phase 3: uncertain jobs and the website "Print Jobs" view

When this companion cannot vouch for a physical outcome (crash after the
spooler accepted a job, completion report failing after retries, or a
backend that still shows the job as printing from a previous session), the
job is parked as **uncertain** — never silently reprinted, never silently
marked failed.

The shopkeeper resolves these jobs on the website: Shop Portal →
**Print Jobs** → "Needs review". They inspect the document, the device and
the selected printer, then record the outcome (Mark printed / Mark failed).
The resolution is audit-logged with their identity. After resolving, create
a fresh print request if the paper was not actually produced.
