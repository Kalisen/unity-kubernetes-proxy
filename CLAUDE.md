# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build and Run

This is a .NET 8 console app (`KubernetesProxy.csproj`, top-level-statements in `immersive/kubernetesproxy/Server.cs`). There is no solution file (`*.sln` is gitignored) and no test project.

- Restore/build: `dotnet build`
- Run (requires a kube config path as the first CLI arg): `dotnet run -- /path/to/kube.config`
- Publish (matches `Properties/PublishProfiles/FolderProfile.pubxml`): `dotnet publish -c Release` → outputs to `bin/Release/net8.0/publish/`
- `kube.config` is gitignored; callers supply their own file at runtime.

The server binds to the hardcoded endpoint `http://192.168.1.200:9999`. Changing the bind address requires editing `Server.cs` (the `ip` / `port` locals).

## Architecture

The proxy exposes an HTTP interface (GenHTTP) in front of the Kubernetes `exec` API (`KubernetesClient`) so that a Unity client can run shell commands inside pod containers over plain HTTP requests.

- **HTTP surface** (`immersive/kubernetesproxy/Server.cs`): two routes built with `GenHTTP.Modules.Functional.Inline` — `GET /` returns a banner, `POST /exec` accepts an `ExecRequest` record `(namespaceName, podName, containerName?, command)` and forwards it to `TerminalService.Exec`. The handler blocks on the task (`commandTask.Wait()`) and returns the command output as the response body.
- **Session cache** (`TerminalService`): a single `Kubernetes` client is built once from the kube config file passed on the command line. `sessions` is a `ConcurrentDictionary` keyed by `"{namespace}/{pod}/{container}"`; each key lazily creates a long-lived `TerminalSession` via `GetOrAdd` so repeated `POST /exec` calls reuse the same interactive shell. Connect failures are swallowed and surfaced as a `"Failed to connect …"` string response.
- **Interactive shell bridge** (`TerminalService.TerminalSession`): calls `client.NamespacedPodExecAsync` with `["/bin/bash", "-i"]` and bridges the three streams through `System.Threading.Channels`:
  - `commands` channel → writer task that appends `COMMAND_TERMINATOR` (`"\r"`) and pushes to stdin.
  - stdout reader task → `outputs` channel (polled every `POLL_PERIOD_MS` = 100 ms, 10 000-char buffer).
  - stderr reader task → `errors` channel (currently not forwarded to callers).
  `Exec` writes a command, then drains `outputs` until a `READ_TIMEOUT_MS` (300 ms) of silence is hit — there is no explicit command-complete marker, so output is delimited purely by that idle timeout. `Connect` additionally drains one initial prompt before returning. Output is post-processed through `StripExtended` (ASCII-only) and `StripControlChars` (drops control chars other than CR/LF) to scrub ANSI/terminal escape sequences before returning.

Key implications when modifying this code:

- The `/exec` handler is synchronous by design (`Wait()` on the task); the single `TerminalSession` per pod is not safe for concurrent `Exec` calls — interleaved writers would race on the shared `outputs` channel.
- Output completeness depends on the `READ_TIMEOUT_MS` idle heuristic; long-running or slow-printing commands can be truncated, and noisy prompts can extend the read window.
- `containerName` is nullable end-to-end; when omitted, the Kubernetes client picks the pod's default container.
