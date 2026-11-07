# Feature Inventory — PrintEXE vs PrintShop SaaS

## Shared Modules (both have)

| Module | Website Page | PrintEXE Screen | API | Status |
| --- | --- | --- | --- | --- |
| Dashboard | `/shop/dashboard` | OverviewView | `GET /shops/me/analytics` + `GET /shops/me/orders?limit=50` | ✅ Both functional |
| Orders | `/shop/orders` | OrdersView | `GET /shops/me/orders` | ✅ Both functional |
| Order Details | `/shop/orders/[id]` | (not yet) | `GET /shops/me/orders/{id}` | ⚠️ Website only |
| Printer Setup | `/shop/printer` | PrintersView | `GET /shops/me/agents` + `POST /agents/register` | ✅ Both functional |
| Daily Reports | `/shop/reports` | (stub) | `GET /shops/me/reports/daily` | ⚠️ Website only |
| Settings | `/shop/settings` | SettingsView | `GET/PATCH /shops/me` | ✅ Both functional |
| Login | `/shop/login` | LoginView | `POST /auth/login` | ✅ Both functional |
| Register | `/shop/register` | (not yet) | `POST /auth/register` | ⚠️ Website only |
| Print Queue | N/A | PrintQueueView | Local SQLite | ✅ PrintEXE only |

## PrintEXE-Only Modules (desktop-specific)

| Module | Purpose | Status |
| --- | --- | --- |
| Print Queue | Local SQLite durable queue of print jobs | ✅ Working |
| Printer Discovery | Windows `System.Printing` enumeration | ✅ Working |
| Test Print | Send a test PDF to a selected printer | ✅ Working |
| Auto-Update | Check GitHub Releases, download, swap files | ✅ Working |
| System Tray | Minimize to tray, context menu | ✅ Working |
| Auto-Start | Per-user Startup folder shortcut | ✅ Working |
| Background Print Engine | Socket.IO + HTTP poll, PDF download, SumatraPDF print | ✅ Working |
| Uncertain Outcome Recovery | Mark jobs as UncertainSpooled, manual reconcile | ✅ Working |

## Modules That Exist in Website but NOT in PrintEXE

| Module | Website? | API? | Can PrintEXE implement? |
| --- | --- | --- | --- |
| Customer management | No page | No API | ❌ No backend support |
| Product/service catalog | No page | No API | ❌ No backend support |
| Invoice generation | No page | No API | ❌ No backend support |
| Expense tracking | No page | No API | ❌ No backend support |
| Inventory management | No page | No API | ❌ No backend support |
| Supplier management | No page | No API | ❌ No backend support |
| Staff management | No page | No API | ❌ No backend support |
| Quotations | No page | No API | ❌ No backend support |
| Audit log viewer | Admin only (`/admin/audit`) | `GET /admin/audit` | ⚠️ Admin-only, not for shopkeepers |
| Subscription management | Read-only on settings | No write API | ⚠️ Read-only |
| UPI QR code | `/shop/settings#qr` | `POST /shops/me/upi-qr` | ⚠️ Could implement if needed |
| Shop registration | `/shop/register` | `POST /auth/register` | ⚠️ Could implement |

## Admin-Only Modules (not for PrintEXE)

| Module | Website Page | API |
| --- | --- | --- |
| Platform Admin | `/admin` | `GET /admin/*` |
| All Shops | `/admin/shops` | `GET /admin/shops` |
| All Orders | `/admin/orders` | `GET /admin/orders` |
| Analytics | `/admin/analytics` | `GET /admin/analytics` |
| Monitoring | `/admin/monitoring` | `GET /admin/monitoring` |
| Audit Log | `/admin/audit` | `GET /admin/audit` |

## Summary

PrintEXE currently has **functional parity** with the website for:
- Login (same auth)
- Dashboard (same analytics + orders data)
- Orders (same list + detail)
- Printer Setup (same agents API)
- Settings (same shop profile)
- Reports (API wired, UI stub)

PrintEXE has **additional desktop-only** features:
- Print queue, printer discovery, test print, auto-update, tray, auto-start

The website does NOT have customer/product/invoice/inventory modules — so PrintEXE cannot implement feature parity for those. The backend would need new API routes + database models first.
