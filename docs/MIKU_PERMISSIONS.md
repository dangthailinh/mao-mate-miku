# Miku permission modes and computer tools

Implemented for the human request dated 2026-10-09. This is an extension of the existing Control Layer, not a replacement for Miku chat, emotion, avatar or provider services.

## How to use

Open the radial wheel and select **QUYỀN**, or type `/permissions`. Choose **Chỉ đọc**, **Hỏi trước** or **Full access**. Default: Hỏi trước. Changes cancel the previous task and recycle the Node runtime so a stale profile cannot retain its previous mode.

Natural controls: `cấp full quyền cho miku`, `bật chế độ hỏi trước`, `thu hồi full quyền`, `cho phép lần này`, `từ chối`. Slash alternatives: `/permissions full`, `/permissions ask`, `/permissions read-only`, `/permissions allow <callId>`, `/permissions deny <callId>`.

Use `/workspace` to see the working directory or `/workspace C:\absolute\existing\folder` to select one. Relative tool paths are resolved against it; the workspace is not a filesystem sandbox. Full access can reach other paths allowed by the current Windows account.

Example tasks:

- `Tạo file demo.txt trong workspace với nội dung Xin chào rồi đọc lại để kiểm tra.`
- `Liệt kê thư mục dự án, đọc README và giải thích cách build.`
- `Chạy PowerShell Get-Date và báo kết quả.`
- `Kiểm tra project này rồi chạy build/test, chưa sửa file trước khi báo lỗi.`
- `Miku dừng tác vụ lại đi` or `Miku dừng mọi thứ`.

Do not give full access to tasks involving untrusted downloads, scripts or documents unless you understand the potential effects. Model output and retrieved data do not grant permission. Full access is an execution authorization, not protection against every malicious prompt or model mistake.

## What is actually enabled

| Mode | Read tools | File writes, app/web opens | PowerShell |
|---|---|---|---|
| Chỉ đọc | Direct, bounded | Denied | Denied |
| Hỏi trước | Direct, bounded | Exact one-use approval | Exact one-use approval |
| Full access | Direct, bounded | Direct for the current human task | Direct for the current human task |

Nine host tools: `open_target`, `save_note`, `list_notes`, `read_note`, `list_directory`, `read_file`, `write_file`, `run_powershell`, `open_url`. PowerShell makes the computer agent broader than the old fixed app catalog: it can inspect installed apps, invoke build/test tools and manipulate task files using the current account's ordinary rights.

This is not feature parity with Codex. There is no new universal mouse/keyboard GUI agent, automatic administrator elevation, unlimited autonomous execution or newly connected Gmail/browser account in this change. The Harness framework and the selected AI model are separate. Runtime evidence must name the provider that actually answered.

## Authority and cancellation

Unity is authoritative. The SDK profile and model persona are not the security boundary. `MaoPermissionPolicy` accepts only human controls through the chat entry or local UI. A pending approval binds task ID, call ID, tool, original request and all argument values. Modified arguments, another call or replay cannot reuse it. Model tool schemas contain no field that can change the mode.

The approval panel redacts credential-like values and marks argument previews longer than 12,000 characters as shortened. Approval still covers the complete immutable arguments. Reject and ask for smaller steps if the preview is insufficient. Ask prompts explicitly request a tool call to initiate the real approval flow; a task that requires tools but receives only prose gets at most one no-action retry, never an automatic retry after side effects.

The registry verifies task ownership, running state, duplicate IDs, budgets and audit storage before leasing an execution. Ask transitions the active task into WaitingPermission; status and approval controls do not cancel the chat coroutine. Stop and mode/workspace changes cancel first, invalidate pending grants, then shut down the owned bridge. PowerShell cancellation terminates only the owned process tree. Completed writes and apps already opened are not automatically rolled back.

Limits: 6 tool calls/read-only, 16/ask, 32/full per task; tasks bounded to 90 seconds read-only or 10 minutes ask/full on the bridge. Each tool relay is bounded to 190 seconds, including approval. PowerShell runs for at most 120 seconds per call. These are runaway-control limits, not a disguised catalog restriction.

## Files and credentials

Directory listing is bounded to 100 entries. read_file accepts text files up to 64 KiB and returns up to 10,000 characters, explicitly marking truncation. write_file accepts at most 32,000 characters and requires an explicit overwrite boolean. A replaced file is backed up before its old content is discarded and the new content is read back for verification. Backup limits: 100 files, maximum 16 MiB per previous file; the tool refuses an overwrite if backup cannot be made. Backups are in the user's persistent Miku `MikuFileBackups` directory; the successful result identifies the specific backup.

The direct file tools reject .env/credential paths rather than sending them into AI context. Child PowerShell does not inherit credential-like environment variables. Host and bridge outputs are redacted; audit records metadata rather than command bodies, file contents, prompts or keys. Full PowerShell is deliberately powerful and may still access local data or the network when instructed: do not confuse redaction with a full OS sandbox.

Native SDK shells, MCP resources, Office/workspace plugins, telemetry/session persistence and unrelated profile producers remain disabled. The only shell integration is the Unity-mediated tool. HHTech deferred release policy is preserved; no HHTech Codex installer or Codex configuration changes are used.

## Verification and release

Node: `npm.cmd test` from Tools/MikuHarness. Unity: MaoControlLayerValidation.RunCoreTests or ValidateAndBuild in batchmode with explicit logFile. Complete trial builds use MaoMateBuild.BuildWindowsApp with a dedicated output directory and --mao-build-control-trial; they never overwrite installed startup ownership or persistent user data.

`--mao-smoke-permissions --mao-smoke-exit` runs only in an isolated control trial: normal chat, pending approval without writes, approval/status preservation, rejection, full file read/write and actual PowerShell, read-only denial, downgrade cancellation. It writes QA JSON and panel screenshots to isolated trial data. `--mao-permissions-status --mao-smoke-exit` is a read-only installation probe: it reports mode and nine registered tools and captures the panel without granting rights or launching a task.

The exact result and installed build are recorded in the dated release report. A successful source compile, Node test or HTTP request alone is not an integration claim.

## Dependency audit caveat

The pinned SDK graph currently reports 13 advisories (6 high, 7 moderate, 0 critical), including propagated parent-package findings. Concrete findings are the MCP OAuth credential-routing advisory and fflate ZIP64 parsing advisory. sdk-minimal does not load the Office skill or remote MCP client; Miku additionally disables mcp-resources and does not forward Office runtime environment variables. These paths are not enabled by Full access. This is a reachability triage, not a claim that the dependency tree has zero vulnerabilities; reassess before enabling MCP or Office or upgrading the pinned SDK.
