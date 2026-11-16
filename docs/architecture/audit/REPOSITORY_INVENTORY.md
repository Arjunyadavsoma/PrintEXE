# Repository Inventory

## printshop-saas

| Property | Value |
|---|---|
| Remote | `https://github.com/Arjunyadavsoma/printshop-saas.git` |
| Branch | `main` |
| Latest commit | `a968266` — "Add 4 new tables + tokenHash to setup-database endpoint" |
| Uncommitted changes | None (clean) |
| Framework | Next.js 16.1.1 (App Router) |
| Language | TypeScript 5 |
| Runtime | Bun (latest) |
| Database | PostgreSQL (Supabase) via Prisma 6.11 |
| ORM | Prisma 6.11.1 |
| UI | React 19, TailwindCSS 4, Radix UI, shadcn/ui |
| State | Zustand 5.0.6, TanStack Query 5.82, TanStack Table 8.21 |
| Forms | React Hook Form 7.60 + Zod 4.0 |
| Realtime | Socket.IO 4.8.4 |
| Storage | Supabase (primary), R2/S3 (via adapter) |
| Cache | Upstash Redis (optional), in-memory (default) |
| Payments | PhonePe + Paytm webhooks, UPI deep-link via upipay |
| Testing | Vitest 5.0 + Playwright 1.65 |
| CI | GitHub Actions (ci.yml, cleanup-cron.yml) |
| Deployment | Vercel (region: bom1 — Mumbai) |
| API routes | 71 total |
| Prisma models | 15 |
| Migrations | 2 (init + add_delivery_and_sync) |

### Directory structure
```
src/
├── app/          — Next.js App Router (pages + API routes)
├── components/   — React UI components
├── hooks/        — Custom React hooks
├── lib/          — Business logic, adapters, services, auth, db
├── middleware.ts — Next.js middleware
mini-services/
├── realtime-service/  — Socket.IO server (ports 3003/3004)
├── native-printer/    — Print agent (Bun/TypeScript)
├── print-agent/       — Another print agent variant
packages/
├── core/        — Domain interfaces, errors, constants
├── adapters/    — Concrete adapter implementations
prisma/
├── schema.prisma  — 325 lines, 15 models
├── migrations/   — 2 migrations
```

## PrintEXE

| Property | Value |
|---|---|
| Remote | `https://github.com/Arjunyadavsoma/PrintEXE.git` |
| Branch | `main` |
| Latest commit | `199f04d` — "v0.5.5: Fix P0 SQLCipher init ordering + P0 destructive migration test isolation" |
| Uncommitted changes | 1 untracked file (docs/architecture/audit/) |
| Framework | .NET 10 + WPF |
| Language | C# 13 |
| MVVM | CommunityToolkit.Mvvm 8.4.0 |
| DI | Microsoft.Extensions.DependencyInjection 10.0.2 |
| Database | SQLite via Microsoft.Data.Sqlite 10.0 + SQLCipher 2.1.11 |
| Logging | Serilog 4.2.0 |
| Realtime | SocketIOClient 3.1.2 |
| PDF printing | SumatraPDF (bundled CLI) |
| Tray | H.NotifyIcon.Wpf 2.4.1 |
| Auto-update | GitHub Releases API + .bat file swap |
| Installer | WiX v5.0.2 (MSI + optional bootstrapper) |
| CI | GitHub Actions (build-windows.yml on windows-latest) |
| Solution | 6 projects + 2 test projects |
| C# files | 89 |
| XAML files | 18 |
| C# lines | 11,650 |
| XAML lines | 2,102 |
| Test files | 8 (48 tests total) |
| Documentation | 23 Markdown files |
| Published releases | 21 (v0.1.2 through v0.5.5) |
