using System;
using System.Diagnostics;
using System.IO;
using MaoIntegration;
using UnityEngine;
using Debug = UnityEngine.Debug;

public sealed class MaoComputerAgentRuntime : MonoBehaviour
{
    private const string EnabledPreference = "MaoComputerAgent.Enabled";
    private const long MaxAuditBytes = 512 * 1024;
    private static readonly TimeSpan MinimumExecutionInterval = TimeSpan.FromSeconds(2);
    private bool agentEnabled;
    private DateTime lastExecutionUtc = DateTime.MinValue;

    public bool Enabled => agentEnabled;
    public bool LastExecutionSucceeded { get; private set; }
    private string AuditPath => Path.Combine(Application.persistentDataPath, "mao-computer-agent-audit.jsonl");

    private void Awake()
    {
        agentEnabled = PlayerPrefs.GetInt(EnabledPreference, 0) == 1;
        Debug.Log("[Mao Computer] event=initialized enabled=" + Enabled.ToString().ToLowerInvariant() +
                  " policy=catalog execution=permission-gated native_shell=false");
    }

    public bool TryHandleUserCommand(string input, out string response)
    {
        response = null;
        MaoComputerCommand command;
        if (!MaoComputerCommandParser.TryParse(input, out command)) return false;

        switch (command.Kind)
        {
            case MaoComputerCommandKind.Enable:
                SetEnabled(true);
                response = "Đã bật Computer Agent. Quyền chạy vẫn theo chế độ anh chọn trong /permissions nha.";
                return true;
            case MaoComputerCommandKind.Disable:
                SetEnabled(false);
                response = "Đã tắt Computer Agent.";
                return true;
            case MaoComputerCommandKind.Status:
                response = Enabled
                    ? "Computer Agent đang bật. Mở app/web theo quyền anh chọn trong /permissions; tác vụ file và PowerShell đi qua Harness."
                    : "Computer Agent đang tắt. Gõ /computer on để bật.";
                return true;
            case MaoComputerCommandKind.RequestAction:
                response = ExecuteModelAction(command.Action);
                return true;
            default:
                return false;
        }
    }

    public string ExecuteModelAction(MaoComputerAction action)
    {
        var harness = GetComponent<MaoHarnessRuntime>();
        if (harness == null || harness.Permissions.Mode != MaoPermissionMode.FullAccess)
        { LastExecutionSucceeded = false; return "Thao tác mở app/web phải qua Harness để kiểm tra quyền. Anh dùng yêu cầu thông thường hoặc chọn quyền trong /permissions nhé."; }
        return ExecuteAllowlistedAction(action);
    }
    internal string ExecuteAuthorizedAction(MaoComputerAction action) => ExecuteAllowlistedAction(action);

    private void SetEnabled(bool enabled)
    {
        agentEnabled = enabled;
        PlayerPrefs.SetInt(EnabledPreference, enabled ? 1 : 0);
        PlayerPrefs.Save();
        WriteAudit(enabled ? "enabled" : "disabled", null, true);
    }

    private string ExecuteAllowlistedAction(MaoComputerAction action)
    {
        LastExecutionSucceeded = false;
        if (!Enabled) return "Computer Agent đang tắt. Gõ /computer on trước nếu bạn muốn Miku thực hiện thao tác này.";
        MaoComputerAction safeAction;
        if (!TryRevalidate(action, out safeAction))
        {
            WriteAudit("rejected", action, false);
            return "Miku đã từ chối thao tác vì mục tiêu không nằm trong danh sách an toàn.";
        }
        if (DateTime.UtcNow - lastExecutionUtc < MinimumExecutionInterval)
            return "Miku đang chờ một chút để tránh mở ứng dụng liên tục.";

        try
        {
            string executionTarget;
            if (!TryResolveExecutionTarget(safeAction, out executionTarget))
            {
                WriteAudit("execution_target_missing", safeAction, false);
                return "Miku không tìm thấy " + safeAction.DisplayName + " trên máy này.";
            }
            Process.Start(new ProcessStartInfo
            {
                FileName = executionTarget,
                UseShellExecute = true,
                CreateNoWindow = true
            });
            lastExecutionUtc = DateTime.UtcNow;
            LastExecutionSucceeded = true;
            WriteAudit("executed", safeAction, true);
            return "Miku đã mở " + safeAction.DisplayName + " cho Linh rồi nè.";
        }
        catch (Exception exception)
        {
            WriteAudit("execution_failed_" + exception.GetType().Name, safeAction, false);
            Debug.LogWarning("[Mao Computer] event=execution_failed target=" + safeAction.TargetId +
                             " error_type=" + exception.GetType().Name);
            return "Miku không thể mở " + safeAction.DisplayName + " trên máy này.";
        }
    }

    private static bool TryRevalidate(MaoComputerAction candidate, out MaoComputerAction safeAction)
    {
        safeAction = null;
        if (candidate == null) return false;
        bool found;
        switch (candidate.Type)
        {
            case MaoComputerActionType.OpenApplication:
                found = MaoComputerActionCatalog.TryResolveApplication(candidate.TargetId, out safeAction);
                break;
            case MaoComputerActionType.OpenWebsite:
                found = MaoComputerActionCatalog.TryResolveWebsite(candidate.TargetId, out safeAction);
                break;
            case MaoComputerActionType.OpenWindowsSettings:
                found = MaoComputerActionCatalog.TryResolveSettings(candidate.TargetId, out safeAction);
                break;
            default:
                return false;
        }
        return found && safeAction != null &&
               string.Equals(safeAction.LaunchTarget, candidate.LaunchTarget, StringComparison.Ordinal);
    }

    private static bool TryResolveExecutionTarget(MaoComputerAction action, out string executionTarget)
    {
        executionTarget = null;
        if (action == null) return false;
        if (action.Type != MaoComputerActionType.OpenApplication)
        {
            executionTarget = action.LaunchTarget;
            return !string.IsNullOrWhiteSpace(executionTarget);
        }

        string executableName;
        string trustedDirectory;
        switch (action.TargetId)
        {
            case "notepad":
                executableName = "notepad.exe";
                trustedDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
                break;
            case "calculator":
                executableName = "calc.exe";
                trustedDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
                break;
            case "explorer":
                executableName = "explorer.exe";
                trustedDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                break;
            default:
                return false;
        }

        if (string.IsNullOrWhiteSpace(trustedDirectory)) return false;
        string fullPath = Path.GetFullPath(Path.Combine(trustedDirectory, executableName));
        string trustedRoot = Path.GetFullPath(trustedDirectory).TrimEnd(Path.DirectorySeparatorChar) +
                             Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(trustedRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
            return false;
        executionTarget = fullPath;
        return true;
    }

    private void WriteAudit(string eventName, MaoComputerAction action, bool success)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AuditPath));
            var file = new FileInfo(AuditPath);
            if (file.Exists && file.Length > MaxAuditBytes)
            {
                string previousPath = AuditPath + ".previous";
                if (File.Exists(previousPath)) File.Delete(previousPath);
                File.Move(AuditPath, previousPath);
            }
            // Avoid reflection-based Newtonsoft serialization here. The
            // shipped Unity/Newtonsoft combination can throw
            // CustomAttributeFormatException while inspecting compiler-
            // generated anonymous types, which used to make every otherwise
            // successful action report audit_failed. This tiny bounded writer
            // emits the fixed audit schema without exposing arbitrary JSON.
            string line = "{\"utc\":" + JsonString(DateTime.UtcNow.ToString("o")) +
                          ",\"eventName\":" + JsonString(eventName) +
                          ",\"action\":" + JsonString(action?.Type.ToString() ?? string.Empty) +
                          ",\"target\":" + JsonString(action?.TargetId ?? string.Empty) +
                          ",\"success\":" + (success ? "true" : "false") + "}";
            File.AppendAllText(AuditPath, line + Environment.NewLine);
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[Mao Computer] event=audit_failed error_type=" + exception.GetType().Name);
        }
    }

    private static string JsonString(string value)
    {
        var builder = new System.Text.StringBuilder((value ?? string.Empty).Length + 2);
        builder.Append('"');
        foreach (char character in value ?? string.Empty)
        {
            switch (character)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (character < 0x20)
                        builder.Append("\\u").Append(((int)character).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    else builder.Append(character);
                    break;
            }
        }
        builder.Append('"');
        return builder.ToString();
    }
}
