# Rich-Text Editor — Audit & Decision

## Current state

The PrintShop Companion does **not** implement a rich-text editor. There is no
`RichTextBox`, no `FlowDocument`, no `WebView2`, and no third-party editor in
the codebase.

The only text-input surfaces are:

1. **`LoginView`** — `TextBox` for email + `PasswordBox` for password.
2. **`OrdersView`** — read-only table; no editable text fields.
3. **`PrintQueueView`** — read-only list; no editable text fields.
4. **`SettingsView`** — labels + checkboxes + buttons; no editable text fields.
5. **`SyncCenterView`** — read-only lists; no editable text fields.

There is no `Notes` field on the Orders view in the companion either. The
SaaS backend's `Order` model has a `notes` field (used by `update_notes`
pending ops in `SyncEngine`), but the companion currently has no UI to view or
edit it.

## Why there is no rich-text editor

The companion's job is to receive and print PDFs that are produced upstream —
the customer uploads a document to the SaaS web app, the web app stores it,
the companion downloads the PDF and submits it to the local printer. The
companion does not author documents; it consumes them.

A rich-text editor would be a **new product feature**, not a fix to existing
functionality. The audit request explicitly says:

> "Do not replace an existing editor without testing its compatibility with
> document storage, printing, and export."

There is no existing editor to replace.

## Decision: out of scope for this audit cycle

Implementing a rich-text editor is out of scope for this audit cycle because:

1. **No existing editor to repair** — the codebase has no editor at all. The
   audit's purpose is to fix existing functionality, not add new product
   features.
2. **Wrong product surface** — the companion is a print receiver, not a
   document editor. The SaaS web app (where customers upload documents) is
   the right place for editing.
3. **No clear use case** — the companion's user (the print shop operator)
   does not need to author documents; they need to print incoming orders.

## If a rich-text editor is needed in the future

The recommended approach would be:

1. **Use the WPF `RichTextBox`** with `FlowDocument` storage. This is the
   native WPF approach, has no third-party dependencies, and supports
   Unicode (including Indian languages) out of the box.
2. **Storage format**: XAML `FlowDocument` for round-trip fidelity. Convert
   to RTF or HTML on export.
3. **Toolbar**: A `ToolBar` with `ToggleButton` items for bold/italic/
   underline/strikethrough, `ComboBox` for font family and size,
   `ColorPicker` for foreground/highlight colors, `RadioButton` for
   alignment, and buttons for bulleted/numbered lists.
4. **Keyboard shortcuts**: Ctrl+B/I/U for bold/italic/underline; Ctrl+Z/Y
   for undo/redo; Ctrl+S for save (intercept and convert to a custom
   command, not the default browser save).
5. **Unicode support**: WPF `RichTextBox` uses the system font fallback
   chain, so Hindi, Tamil, Bengali, Marathi, Telugu, etc. render correctly
   as long as the user has the appropriate fonts installed (Windows 10/11
   ships with Nirmala UI which covers most Indic scripts).
6. **Printing**: A `FlowDocument` can be printed directly via
   `PrintDialog.PrintDocument(flowDocument.DocumentPaginator, "title")`.
   This bypasses the SumatraPDF path used for PDFs but is the native WPF
   printing path.
7. **Persistence**: Save to a `.xaml` file or as a string column in the
   SQLite database (the `OrderRecord` would need a `NotesXaml` column).
8. **Sanitization**: If the document is ever exported as HTML, sanitize
   with a whitelist of allowed tags/attributes (no `<script>`, no inline
   event handlers). The `HtmlSanitizer` NuGet package is the standard.

## What was done in this audit cycle

- **No code was written for a rich-text editor** (it is out of scope).
- This document records the audit finding and the recommended future
  implementation approach.
- A `Notes` field on the Orders view (plain text only, no rich text) would
  be a reasonable smaller-scope addition in a future cycle, since the
  backend already supports `update_notes` pending ops. That too is out of
  scope for this audit cycle.

## Test plan (if a rich-text editor is added in the future)

| Test | Expected |
| --- | --- |
| Type text in English | Text appears in the editor |
| Type text in Hindi (Devanagari) | Text renders with Nirmala UI fallback |
| Type text in Tamil | Text renders with Nirmala UI fallback |
| Apply bold, italic, underline | Selected text formatting changes |
| Save document | Document persists to disk/DB |
| Close and reopen | Document reopens with formatting preserved |
| Print document | Document prints with formatting preserved |
| Export as HTML | HTML contains the formatting; no script tags |
| Paste from Word | Pasted content retains reasonable formatting |
| Paste from browser | Pasted content retains reasonable formatting |
| Large document (100 pages) | Editor remains responsive |
| Undo / Redo | Works correctly |
| Find / Replace | Works correctly |
