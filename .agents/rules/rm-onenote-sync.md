---
trigger: always_on
---

# Project: rmOneNoteSync

## Agent Instructions & Build Rules

You are assisting with a two-part project: a .NET Avalonia desktop app (`app/rmOneNoteSyncApp`) and a native C daemon for the reMarkable tablet (`rm-daemon`).

### 🛠️ Rule: Building the C Daemon (`rm-daemon`)
When you are asked to build, compile, or test changes to the native C codebase inside the `rm-daemon` folder, you MUST use the official reMarkable Codex cross-compilation toolchain. 

**CRITICAL:** The toolchain script spawns background processes that will hang your terminal session indefinitely. To prevent this, you MUST pipe stdin from `/dev/null`. Use this exact command:

```bash
cd rm-daemon && bash -c "source /opt/codex/ferrari/5.5.125/environment-setup-cortexa53-crypto-remarkable-linux && make clean && make all" < /dev/null


### 🛠️ Rule: Building the C# Desktop App (`app/rmOneNoteSyncApp`)
To prevent MSBuild background nodes from hanging the terminal, and to prevent large build outputs from crashing the agent's Language Server (LS_ERROR), you MUST run the build quietly and dump the output to a log file.

**Correct Usage:**
`dotnet build /path/to/project -nr:false > build.log 2>&1`

If you need to verify the build succeeded, check the exit code, or read `build.log` after the command completes. Do not run `dotnet build` without piping the output.