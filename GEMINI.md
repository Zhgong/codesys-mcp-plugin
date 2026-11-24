# Repository Guidelines & Rules

## Git Push Protocol
- **Default Remote & Branch**: Always and only push to the private repository (`origin`, `https://github.com/Zhgong/codesys-mcp-plugin-private.git`) on the `development` branch by default (`git push origin development`).
- **Public Remote (`public`, `https://github.com/Zhgong/codesys-mcp-plugin.git`)**:
  - **CRITICAL RESTRICTION**: NEVER push, sync (`sync_to_public.ps1`), or open PRs to the public repository without explicit, direct user authorization and confirmation in the conversation.
  - The public repository is release-only and strictly gated by user approval.

## Engineering Rules ("Less is More")
- **Engineering Wiki**: Before changing CODESYS reflection, WinForms marshaling, or E2E testing, consult `wiki/README.md` (especially `wiki/02_Codesys_Internals_and_Reflection.md` and `wiki/04_Testing_Strategy_and_Fast_Loop.md`).
1. **Reflect Real DLLs First**: When interacting with CODESYS internals (`PlugIns/*.plugin.dll`, `LacBinaries/GAC_MSIL`), inspect real types/fields via reflection instead of guessing or broadly recursing `Control.Controls` (e.g., read `SynEdControl.Buffer` directly; ignore `IncrementalSearchControl` overlays).
2. **Pair Reflection with Real-DLL Contract Tests**: Every reflection-based integration must have an `[STAThread]` contract test loading the installed CODESYS assemblies (do not rely on mocks alone).
3. **Prefer `SendMessageW` over `SendInput` for Background GUI E2E**: When `GetForegroundWindow() == 0`, `SendInput` fails with `err=5`; locate `SynEdControl._textArea` via `EnumChildWindows` (`_margin + _textArea` geometry) and send `WM_KEYDOWN`/`WM_CHAR` directly. Also keep `CodesysMcpCli.exe` `stdin` open via `Popen` until receiving the target response ID.
4. **Enforce Non-Empty Guards on Source Sync**: Never overwrite non-empty POU declarations/implementations or `.st` files with whitespace-only UI snapshots.
