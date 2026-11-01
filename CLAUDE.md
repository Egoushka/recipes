# CLAUDE.md — recipes

A .NET 10 minimal API: a cooking journal of versioned entries with text and meaning search, an MCP endpoint and an integration for the approvals phone shell. Public repository, MIT.

## Commands

- Build and test: `dotnet build recipes.slnx -c Release` then `dotnet test recipes.slnx -c Release --no-build`
- Format check (CI runs it): `dotnet format recipes.slnx --verify-no-changes`
- In the Claude desktop sandbox `dotnet test` needs the sandbox off (vstest binds a local port), and `dotnet` lives in `$HOME/.dotnet`.

## Rules

- The spec is `docs/specs/2026-11-01-journal-v0.1.md`; each acceptance check there names the tests that prove it. Change the spec with the code.
- One code path: MCP tools and shell actions both call `JournalService` (`src/Recipes/Search`). Do not add a second save path.
- The signature code (`Shell/Signing.cs`) must keep matching approvals `contract/shell/` (`fixtures/signature-vectors.json` is a copy). Change both or neither.
- Screens are A2UI v0.9 documents built in `Shell/ShellScreens.cs`. They are not validated against the shell's schema in this repo's tests (spec A9); check them with approvals' `contract/shell/check-shell.mjs` before relying on a new component.
- Public repo: no hostnames, tokens, tailnet addresses or personal data. The seed is the generic gas-mark scale.
- Every number added (a limit, a window, a constant) carries its reason in a comment or is marked a starting value.
- Commits: Conventional Commits; squash-merge; release-please makes every feature a patch.
