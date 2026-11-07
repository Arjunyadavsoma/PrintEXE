# Security Audit — PrintShop Companion

This document records the security posture of the PrintShop Companion as of v0.4.3.

## Authentication & credentials

| Item | Implementation | Status |
| --- | --- | --- |
| Login | `POST /api/v1/auth/login` with `{ email, password }` against the production SaaS at `https://printshop-saas-three.vercel.app` | ✅ HTTPS enforced |
| Session storage | `ICredentialStore` (`DeviceTokenStore`) DPAPI-encrypts the session at `%AppData%\PrintShop\Companion\device.dat` | ✅ DPAPI-encrypted, not plaintext |
| Auth header | `Authorization: Bearer <accessToken>` set on every authenticated request via `_http.DefaultRequestHeaders.Authorization` | ✅ Standard OAuth 2.0 Bearer |
| Refresh token | `POST /api/v1/auth/refresh` with `{ refreshToken }` | ✅ Standard refresh flow |
| Sign out | `IShopBackend.SignOutAsync()` clears the in-memory session + deletes `device.dat` | ✅ Both memory and disk cleared |
| Token redaction in logs | `DeviceTokenRedactor` Serilog enricher redacts any property whose name contains "Token", "Secret", or "Password" | ✅ Verified |

## HTTPS / TLS

| Item | Status |
| --- | --- |
| Production backend uses HTTPS | ✅ `https://printshop-saas-three.vercel.app` |
| GitHub API uses HTTPS | ✅ `https://api.github.com/repos/Arjunyadavsoma/PrintEXE/releases/latest` |
| `BypassTlsVerification` flag | ✅ Defaults to `false` in `appsettings.json`. Only the dev mock backend flips it (and the dev backend is not shipped to end users). Loud warning logged when enabled. |
| TLS version | .NET 10 default (TLS 1.2 minimum, TLS 1.3 preferred) |

## Update downloads

| Item | Status |
| --- | --- |
| Update ZIP downloaded over HTTPS | ✅ GitHub uses HTTPS for all asset downloads |
| SHA-256 verification of downloaded ZIP | ✅ Implemented in v0.4.3 (`GitHubUpdateService.InstallUpdateAsync` streams bytes through `CryptoStream(SHA256.Create())` and compares against the GitHub asset `digest` field) |
| Mitigation if GitHub didn't provide a digest | ⚠️ Logs a warning and proceeds without verification (cannot verify what doesn't exist) |
| Update server authority | GitHub Releases API is the source of truth — no separate update server to compromise |
| .bat updater signature | ❌ Not signed. A malicious actor with write access to the app directory could replace the .bat. **Mitigation**: prefer the signed MSI installer (signed via `PRINTSHOP_CODE_SIGN_PFX` secret) over the portable ZIP. |

## Local data protection

| Item | Status |
| --- | --- |
| Session tokens | ✅ DPAPI-encrypted at `%AppData%\PrintShop\Companion\device.dat` |
| Print queue DB | ⚠️ SQLite stored at `%AppData%\PrintShop\Companion\queue.db` — not encrypted. Contains job metadata, customer phone numbers, order amounts. |
| PDF cache | ⚠️ Stored at `%AppData%\PrintShop\Companion\pdf-cache\` — not encrypted. Contains the actual PDF documents that customers uploaded. |
| Log files | ✅ Stored at `%AppData%\PrintShop\Companion\logs\companion-YYYYMMDD.log`. Tokens are redacted via `DeviceTokenRedactor`. Customer phone numbers and order amounts MAY appear in debug logs if a developer logs them. |

**Recommendation**: Document the data-at-rest posture clearly to users. The
companion runs on a single user's Windows account; the SQLite queue and PDF
cache inherit the OS file-system permissions of that user's profile. Users who
need stronger protection should use BitLocker or another full-disk-encryption
solution.

## Input validation

| Item | Status |
| --- | --- |
| URL validation | ✅ `Uri.TryCreate` used for PDF URLs |
| Path traversal | ✅ `Path.Combine` used for all file paths; user-supplied paths are not concatenated |
| JSON deserialization | ✅ `System.Text.Json` (no `JsonConvert.DeserializeObject<object>` of untrusted input) |
| SQL injection | ✅ All SQLite queries use parameterized commands (`@param` syntax); no string-concatenation of SQL |
| HTML/JS injection | ✅ N/A — companion has no HTML rendering surface (no WebView2) |

## Logging

| Item | Status |
| --- | --- |
| Log location | `%AppData%\PrintShop\Companion\logs\companion-YYYYMMDD.log` |
| Log retention | 14 days (configured in `appsettings.json`) |
| Sensitive data in logs | ✅ `DeviceTokenRedactor` redacts any property containing "Token", "Secret", "Password" |
| Customer PII in logs | ⚠️ Phone numbers and order amounts MAY appear in debug-level logs if a developer logs them. Production deployment uses Information level (not Debug), so this is unlikely. |

## Dependency vulnerabilities

| Item | Status |
| --- | --- |
| `Newtonsoft.Json` | ✅ Pinned to 13.0.4 (latest safe; the 9.0.x transitive was vulnerable) |
| `SQLitePCLRaw.lib.e_sqlite3` | ✅ Pinned to 3.50.3 (latest safe; the 2.1.x transitive was vulnerable) |
| `SocketIOClient` | ✅ Pinned to 3.1.2 (drops the legacy `System.Net.WebSockets.Client.Managed` chain) |
| `System.Security.Cryptography.ProtectedData` | ✅ Framework reference (no explicit PackageReference) |
| `System.Drawing.Common` | ✅ Framework reference (no explicit PackageReference) |
| Vulnerability scan | Run `dotnet list package --vulnerable` on Windows to verify; the v0.4.2 GitHub Actions build log shows 0 NU1903 warnings on the explicit overrides |

## What was done in this audit cycle (Phase 8)

- **SHA-256 verification of update downloads** added in `GitHubUpdateService.InstallUpdateAsync`. The download stream is wrapped in `CryptoStream(SHA256.Create())` so the hash is computed in a single pass while the bytes are written to disk. The computed hash is compared against the GitHub asset's `digest` field. If they don't match, the install aborts with a clear error message showing the expected and actual hashes.

## What was NOT done (out of scope or requires Windows)

- No encryption of the SQLite queue at rest (would require SQLCipher or a custom layer).
- No signing of the .bat updater (would require a code-signing certificate the project does not have).
- No DPI / accessibility verification (requires Windows runtime).
- No code signing of the portable ZIP distribution (MSI installer is signed when `PRINTSHOP_CODE_SIGN_PFX` is configured; portable ZIP is not).
