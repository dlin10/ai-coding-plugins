# Strip the Store alias directory from the PATH of the codex process

Codex chooses the shell it runs a model's commands in, and on Windows it wants PowerShell. On a
machine where PowerShell 7 was installed from the Microsoft Store, `PATH` resolves `pwsh` to
`%LOCALAPPDATA%\Microsoft\WindowsApps\pwsh.exe`, a zero-byte reparse-point stub that Windows
resolves only for the interactive user token. Codex launches every command through
`CreateProcessAsUserW` with a restricted token, and a restricted token cannot traverse a Store app
execution alias, so Windows answers `ERROR_ACCESS_DENIED`. Every command fails, the build reports
`blocked`, and the working tree is never touched. Issue #58 measured it on `codex` 0.147.0 and
Windows 11 26200, where `C:\Program Files\PowerShell\7\pwsh.exe` does not exist and the alias is the
only `pwsh` on `PATH`.

This is not exotic. It is what a current Windows 11 looks like when PowerShell 7 came from the
Store rather than the installer, so a fix that repairs one machine repairs the wrong thing.

Codex offers no lever for the shell, established three ways rather than assumed. No configuration
key names one: `shell_path`, `default_shell`, `shell_executable`, `windows_shell` and `shell.path`
are all refused as unknown fields under `--strict-config`, and the only keys with *shell* in the
name are `shell_environment_policy` and `allow_login_shell`. The `shell_type` the model catalogue
reports is identical for every model and names the tool, not the binary. And
`shell_environment_policy.set.PATH` was measured and does **not** move the choice: given a sanitised
`PATH` through that key, codex still resolved the alias, so it resolves the shell from its own
process environment before that policy is applied.

That leaves exactly one lever, which is the environment of the codex process this server starts. So
forge removes the `WindowsApps` entries from the `PATH` it passes to codex. Codex then falls through
to `C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe`, a real executable a restricted token
can launch, and which is already the second entry in the shell table codex carries. The failure mode
is discriminating rather than lucky: with the directory present the launch fails with access denied,
with it absent an explicit `pwsh` fails with *file not found*, naming the alias as the cause.

The sandbox survives the substitution. Under Windows PowerShell 5.1 a command writes and reads
inside the workspace, exit codes propagate unchanged, and a write outside the writable root is still
denied. The shell changed; the containment did not.

The edit reaches only the process forge spawns. The machine's `PATH` is not touched, and the user's
own codex sessions are unaffected.

Because a repair can itself fail, the vendor's probe also stops taking *installed and signed in* as
*able to work*. It checks locally that the shell codex would choose is a real executable rather than
an alias stub, and reports the vendor unavailable with that reason when it is not. This is a local
filesystem check with no model turn: the probe runs for every vendor in the background at
`forge.begin`, and spending a paid turn there to diagnose would be a poor trade. It is a second
line, not the repair.

**Rejected: telling the user to install PowerShell 7.** It fixes the machine it is run on and
nobody else, and it is a system change this server has no business making. It remains available as
an improvement, and after the repair it buys back what the repair costs.

**Rejected: giving the builder `danger-full-access`.** It removes the symptom everywhere at once,
because the restricted token is what cannot traverse the alias. It also masks this whole class of
failure on every other machine and surrenders the containment the critic and builder guarantees rest
on, for a bug in a path lookup.

**Rejected: pointing codex at `cmd`.** There is no mechanism. `codex sandbox cmd /c …` succeeds
because that is a caller handing codex a complete command line; when the model runs a command, codex
wraps it in PowerShell with its own encoding preamble, and nothing configurable changes that.

## The costs, named

Inside a codex session `winget`, `python3`, `wt`, `ngrok`, `ubuntu` and `pwsh` stop resolving,
because the alias directory was their only entry on `PATH`. `python`, `wsl`, `bash`, `notepad` and
`powershell` survive on system copies. A plan whose task needs one of the lost tools must name its
real path.

The shell a codex builder gets on such a machine is Windows PowerShell 5.1, not 7. Nothing in this
repository depends on a 7-only construct, and a user who installs PowerShell 7 into
`C:\Program Files` gets it back with no change here, because codex prefers that path over the
fallback.

The repair is Windows-shaped and named for a Windows directory. This plugin is Windows x64 only, so
it has no other platform to be wrong on today; a port would have to revisit it.
