# Clippy

A clipboard manager for Windows that replaces **Win+V** with a fast, dark popup
that opens at your cursor. Copy something, press the hotkey, pick a clip, and
keep working. Clippy stays open after a paste so you can drop several clips in a
row, and everything it stores is encrypted before it touches disk.

- **2.6 MB, one exe.** No installer, no service, no Electron runtime.
- **Encrypted by default.** AES-256-GCM payloads, with the master key wrapped by
  Windows DPAPI for your account.
- **Local only.** No network code, no accounts, no cloud sync, no telemetry.
- **Pinned clips never expire.** Keep passwords, addresses and snippets forever.
- **Encrypted nicknames.** Label a pinned secret "email password" and the list
  shows the label instead of the text.
- **Any global hotkey.** Win+V out of the box; bind Ctrl+Shift+V, F8 or anything
  else in Settings.
- **Search that keeps up.** Text, HTML, RTF, images and file lists are all
  searchable by content, source app or nickname.

## Download and install

1. Download one of these:
   - [`dist/Clippy.exe`](dist/Clippy.exe) — **2.6 MB**, needs the
     [.NET 10 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0).
   - [`Clippy-Portable.exe`](../../releases/latest/download/Clippy-Portable.exe) —
     **48 MB**, self-contained; no runtime install, runs on any Windows 10/11 x64
     machine.
2. Put the exe anywhere you like and run it. There is no installer.
3. Press **Win+V**. Clippy sits in the tray and starts with Windows by default.

The exe is not code-signed, so Windows SmartScreen may show a warning on first
run. Choose **More info → Run anyway** if you downloaded it from this repository
or its Releases page. The full source is here to audit.

Windows 10 or Windows 11, x64. Settings and history are shared by both builds.

## Why Clippy instead of another clipboard manager

Windows' own Win+V is a good start, but it has no pinning, no labels, no custom
shortcut, and Microsoft syncs clipboard history across devices unless you turn
that off. Full clipboard managers such as Ditto or CopyQ are powerful, but they
usually carry an installer, more UI, and plaintext history on disk.

Clippy is aimed at the gap between those two:

| | Clippy | Typical clipboard manager |
|---|---|---|
| Install | One 2.6 MB exe, no installer or service | Installer, service, or bundled runtime |
| Storage | AES-256-GCM, DPAPI-wrapped key | Often plain SQLite, JSON or text files |
| Network | None at all | Update checks, sync or cloud features |
| Pinned clips | Exempt from age, count and size limits | Often share the normal retention rules |
| Secret labels | Encrypted nicknames, searchable | Folders, tags or plain labels |
| Popup behaviour | Stays open for several pastes in a row | Usually closes after one pick |
| Memory | About 16 MB private with a small history | Tens to hundreds of MB |
| License | MIT, full source | Mixed |

The design choices are deliberate:

- **A hotkey that feels native.** Clippy asks Windows for the shortcut first. If
  another program already owns it, Clippy can suppress the chord with a
  low-level keyboard hook, so the popup still opens.
- **Secrets stay secret.** Nicknames are encrypted in their own columns, and the
  preview is hidden while a nickname is set. A pinned password is one hotkey,
  one search and one Enter away, without appearing in the list.
- **No surprise retention.** Pinned clips are only removed when you delete them.
- **Small on purpose.** The UI is plain WinForms. Startup and idle CPU are
  effectively zero; a scored search over the capped history takes well under a
  millisecond.

## Using it

| Input | Action |
|-------|--------|
| Win+V (or your hotkey) | Open or close the popup at the cursor |
| Type | Filter by text, source app, file name or nickname |
| Up / Down, PgUp / PgDn | Move the selection |
| Enter | Copy the clip and paste it into the previous window |
| Shift+Enter | Copy as plain text only |
| Click a row | Copy and paste; the popup stays open |
| Shift+click a row | Paste as plain text |
| Right-click a row | Pin / unpin, add or change a nickname, delete |
| Ctrl+P | Pin or unpin the selected clip |
| Ctrl+Del | Delete the selected clip |
| Esc, X, or the hotkey again | Close the popup |
| Drag the header | Move the popup |

The tray icon opens the popup on left click. Its menu has Open, Pause capture,
Settings, Clear history and Exit.

### Custom global hotkey

Open **Settings → Global hotkey**, click the field, and press the combination
you want. Hold at least one of Ctrl, Alt, Shift or Win; function keys such as F8
also work on their own. Backspace resets it to Win+V. The new shortcut is
registered immediately when you save.

If the combination belongs to another application, Clippy falls back to a
keyboard hook instead of failing. With the default Win+V, Clippy may offer once
to turn off Windows clipboard history and cloud sync so it can own the shortcut
natively. No registry value is changed without that confirmation.

### Nicknames for pinned secrets

Right-click any text clip and choose **Add nickname…**. The nickname is
encrypted and shown instead of the clip text; search matches it, and the preview
stays hidden. Clear the field to remove the nickname.

For a password: copy it, pin the clip with Ctrl+P, right-click it, give it a name
like "router admin", and delete it from the clipboard history if you want.
Pinned clips are not touched by Clear history or by retention.

## Privacy and storage

Everything lives in `%LocalAppData%\Clippy\`:

| File | Contents |
|------|----------|
| `clippy.db` | Encrypted clips in SQLite (WAL mode) |
| `key.bin` | Random 32-byte master key, wrapped with DPAPI |
| `settings.json` | Plain settings: limits, exclusions, hotkey |
| `logs\clippy.log` | Counts, kinds and errors only; never clipboard contents |

Every payload, preview, nickname and source label gets a fresh AES-256-GCM
nonce. Dedupe digests are keyed HMAC-SHA256, so the database cannot be
dictionary-attacked offline. Copying the database to another Windows account
produces unreadable rows.

The database stores a keyed fingerprint of the master key. If the key was
replaced or the database came from another account, Clippy reports it and offers
to move the old file aside as `clippy.db.orphaned-<time>` rather than writing new
clips next to unreadable ones. One damaged row is skipped and logged; it never
hides the rest of the history.

Capture respects the markers password managers and browsers use:
`ExcludeClipboardContentFromMonitorProcessing`, `Clipboard Viewer Ignore` and
`CanIncludeInClipboardHistory=0`, whether the marker arrives as a DWORD, bytes or
an OLE stream. Paste-back sets the same markers, so Windows clipboard history and
cloud sync do not keep a second copy. Processes listed in Settings are skipped
before the clipboard is read. The default exclusion list covers KeePass,
KeePassXC, 1Password, Bitwarden, Keeper and mstsc. Payloads above 32 MB are
skipped.

SQLite runs with `secure_delete=ON`, and Clear history vacuums the file so
deleted rows do not linger in free pages.

## Retention and pinning

Defaults: 500 clips, 30 days, 512 MB. All three are editable in Settings.

Pinned clips are exempt from all three limits. The storage cap applies to
unpinned history; a large set of pinned images can exceed it by design. Pinned
clips are removed only by an explicit delete.

Duplicate copies do not add rows. The check ignores the source window: the same
text copied from two apps is one clip whose source updates to the latest copy.
The existing row moves to the top and its use count increments.

## Build from source

Requires the .NET 10 SDK.

```powershell
dotnet build
dotnet test
dotnet publish src\Clippy.App -c Release -o artifacts\publish
dotnet publish src\Clippy.App -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o artifacts\portable
```

The first publish produces `artifacts\publish\Clippy.exe` as a single
framework-dependent file (2.6 MB). The second produces the self-contained
`Clippy-Portable.exe` (48 MB). The checked-in [`dist/Clippy.exe`](dist/Clippy.exe)
is the small build from the tagged source.

Startup flags:

| Flag | Effect |
|------|--------|
| `--open` | Show the popup |
| `--settings` | Open the settings sheet |

Launching the exe again does not start a second copy. It signals the running
instance to show the popup, or the settings sheet when `--settings` is passed.

### Layout

```
src/Clippy.Core          Models, crypto, SQLite store, search, settings, hotkey parsing
src/Clippy.App           WinForms shell, tray, hotkey, capture, popup, settings
tests/Clippy.Core.Tests  xUnit tests for the core
dist                     ready-to-run exe
tools                    Desktop verification helper
```

`Clippy.Core` has no WinForms dependency. `Clippy.App` owns every window, hook
and clipboard call.

## Tests

`dotnet test` runs 65 tests against the core library:

- AES-GCM round trip, tamper rejection, nonce uniqueness, key separation
- CF_HTML byte offsets with non-ASCII text
- Search ranking: prefix over substring, nickname priority, pinned tie-break,
  fuzzy subsequence
- Hotkey binding: parsing, formatting, reserved combinations, function keys
- Store: encrypted payload and nickname round trips, no plaintext on disk,
  dedupe, pin survival against expiry, count pruning and the byte cap, clear
  semantics
- Key handling: stable fingerprint reload, wrong-key rejection, adoption of older
  databases, damaged row skipping, moving an unreadable database aside
- Migrations: plaintext source columns sealed in place, nickname columns added,
  vacuum verified
- Plain text: HTML paragraphs and line breaks kept, inline runs collapsed, RTF
  font tables and destinations stripped

The UI has no automated tests. `tools\verify-popup.ps1` drives it on a desktop:
it can move the cursor, send Win+V, type keys and save a screenshot.

## Limits

- OCR is not implemented, so text inside screenshots is not searchable.
- No sync, export, import or cloud history.
- The small exe needs the .NET 10 Desktop Runtime; the portable build does not.
- The exes are unsigned.
- Pasting into elevated windows needs Clippy to run elevated too; Windows blocks
  synthetic input across integrity levels.
- One instance per user session; fast user switching is not handled.
- The popup has no password lock. Anyone at the unlocked desktop can open it.
- Image and file clips are stored, but previews are minimal: a small thumbnail
  for images, file names for file lists.
- Trimming is off because WinForms is not trim-safe; the portable build carries
  the whole runtime (48 MB) instead of trimming it.

## License

MIT. See [LICENSE](LICENSE).
