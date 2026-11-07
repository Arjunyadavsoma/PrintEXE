# Adding the GitHub Actions Workflow (one-time setup)

The `.github/workflows/build-windows.yml` file was NOT included in the initial
push because the Personal Access Token used for that push did not have the
`workflow` scope. GitHub rejects PATs without the `workflow` scope when they
try to create or update any file under `.github/workflows/`.

You have two options to add the workflow.

## Option A — Add the workflow via the GitHub web UI (no new token needed)

1. Open your repo: <https://github.com/Arjunyadavsoma/PrintEXE>
2. Click **Add file → Create new file**.
3. In the filename box, paste exactly: `.github/workflows/build-windows.yml`
   (the directories will be created automatically).
4. In the editor, paste the full contents of the file at:
   `apps/windows-companion/.github/workflows/build-windows.yml`
   on your local machine.
5. At the bottom, click **Commit new file**.

The workflow is now active. Pushing to `main` or opening a PR will trigger it.

## Option B — Regenerate the PAT with `workflow` scope and re-push

1. Go to <https://github.com/settings/personal-access-tokens>.
2. Click **Generate new token (fine-grained)**.
3. Under **Permissions → Repository permissions → Workflows**, select **Read and write**.
4. (Also keep Contents: Read and write, Metadata: Read.)
5. Generate the token, copy it, and from your local clone run:

   ```powershell
   cd apps\windows-companion
   git add .github/workflows/build-windows.yml
   git commit -m "Add GitHub Actions workflow: build-windows.yml"
   git push
   ```

   (Replace `git push` with the credential-helper push command from your chat
   history if you want to use the inline-token method.)

**Recommendation:** Option A is faster and doesn't require rotating the token.

## What the workflow does

When triggered (push to `main`, PR, or manual run), it:

1. Checks out the repo on a `windows-latest` runner.
2. Installs the .NET 10 SDK.
3. Installs WiX v5 as a `dotnet tool`.
4. `dotnet restore` + `dotnet build` + `dotnet test`.
5. Publishes the WPF app as self-contained single-file (no .NET install needed on target).
6. Downloads SumatraPDF.exe into `installer/redist/`.
7. Builds the `.msi` via `wix build`.
8. Optionally builds the `.exe` bootstrapper.
9. Optionally signs the MSI with an EV code-signing cert (if you add the
   `PRINTSHOP_CODE_SIGN_PFX` and `PRINTSHOP_CODE_SIGN_PWD` repo secrets).
10. Generates SHA-256 checksums.
11. Uploads the MSI, EXE, and checksums as build artifacts.
12. On tag push (`v*`), creates a GitHub Release with the artifacts attached.

See `.github/workflows/build-windows.yml` in your local clone for the full source.
