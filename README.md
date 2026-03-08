# Codex Home Manager

A WinForms helper for switching `CODEX_HOME`, preparing a hybrid Codex home, and importing selected sessions into a target home.

## Features

- Load sessions from a source `CODEX_HOME`
- Prepare a hybrid home from `state` + `auth`
- Import one selected session into the target home
- Add workspace hints to the target `.codex-global-state.json`
- Close the running Codex app and relaunch it with the target `CODEX_HOME`

## Run

```powershell
dotnet run --project C:\codex\CodexHomeManager
```

## Build

```powershell
dotnet build C:\codex\CodexHomeManager
```

## Notes

- Close the running Codex app before launching with a different `CODEX_HOME`.
- The app auto-loads `OPENAI_API_KEY` from the target `auth.json` when launching Codex.
- The helper updates `session_index.jsonl`, `history.jsonl`, and `state_5.sqlite` in the target home.
