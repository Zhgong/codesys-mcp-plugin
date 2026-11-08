# Repository Guidelines & Rules

## Git Push Protocol
- **Default Remote & Branch**: Always and only push to the private repository (`origin`, `https://github.com/Zhgong/codesys-mcp-plugin-private.git`) on the `development` branch by default (`git push origin development`).
- **Public Remote (`public`, `https://github.com/Zhgong/codesys-mcp-plugin.git`)**:
  - **CRITICAL RESTRICTION**: NEVER push, sync (`sync_to_public.ps1`), or open PRs to the public repository without explicit, direct user authorization and confirmation in the conversation.
  - The public repository is release-only and strictly gated by user approval.
