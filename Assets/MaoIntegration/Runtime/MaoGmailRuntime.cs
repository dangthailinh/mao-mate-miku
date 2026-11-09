using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using MaoIntegration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;
using Debug = UnityEngine.Debug;

public sealed class MaoGmailRuntime
{
    private const string ClientIdPreference = "MaoGmail.ClientId";
    private const string ClientIdEnvironment = "MAO_GMAIL_CLIENT_ID";
    private const int RequestTimeoutSeconds = 20;
    private const int OAuthTimeoutSeconds = 180;
    private const int MaximumMessages = 5;
    private const string GmailApiBase = "https://gmail.googleapis.com/gmail/v1/users/me/messages";

    private string accessToken = string.Empty;
    private string refreshToken = string.Empty;
    private DateTime accessTokenExpiresUtc = DateTime.MinValue;
    private IntPtr activeListener = MaoWindowsLoopbackCallback.InvalidSocket;
    private bool operationInProgress;

    private string CredentialPath => Path.Combine(Application.persistentDataPath, "mao-gmail-credential.json");
    public bool IsConfigured => MaoGmailSecurity.IsValidDesktopClientId(GetClientId());
    public bool IsConnected => !string.IsNullOrWhiteSpace(refreshToken);

    public MaoGmailRuntime()
    {
        LoadCredential();
        Debug.Log("[Mao Gmail] event=initialized configured=" + IsConfigured.ToString().ToLowerInvariant() +
                  " connected=" + IsConnected.ToString().ToLowerInvariant() + " scope=readonly");
    }

    public void Dispose()
    {
        StopListener();
        ClearAccessToken();
    }

    public bool TryParseCommand(string input, out MaoGmailCommand command)
    {
        return MaoGmailCommandParser.TryParse(input, out command);
    }

    public IEnumerator ExecuteRoutine(MaoGmailCommand command, Action<string> onResult)
    {
        if (command == null)
        {
            onResult?.Invoke("Miku không hiểu yêu cầu Gmail này.");
            yield break;
        }
        if (operationInProgress && command.Kind != MaoGmailCommandKind.Status)
        {
            onResult?.Invoke("Miku đang xử lý một yêu cầu Gmail khác rồi nè.");
            yield break;
        }

        switch (command.Kind)
        {
            case MaoGmailCommandKind.ConfigureClient:
                ConfigureClient(command.Value, onResult);
                yield break;
            case MaoGmailCommandKind.Status:
                onResult?.Invoke(BuildStatusReply());
                yield break;
            case MaoGmailCommandKind.Connect:
                yield return ConnectRoutine(onResult);
                yield break;
            case MaoGmailCommandKind.Disconnect:
                yield return DisconnectRoutine(onResult);
                yield break;
            case MaoGmailCommandKind.ListUnread:
                yield return ReadMessagesRoutine("label:inbox is:unread", "Mail chưa đọc", onResult);
                yield break;
            case MaoGmailCommandKind.SearchSender:
                yield return ReadMessagesRoutine(MaoGmailCommandParser.BuildSenderQuery(command.Value),
                    "Mail từ " + command.Value, onResult);
                yield break;
            default:
                onResult?.Invoke("Miku chưa hỗ trợ thao tác Gmail này.");
                yield break;
        }
    }

    private void ConfigureClient(string clientId, Action<string> onResult)
    {
        if (!MaoGmailSecurity.IsValidDesktopClientId(clientId))
        {
            onResult?.Invoke("OAuth Client ID chưa đúng định dạng Desktop của Google.");
            return;
        }
        DeleteCredential();
        PlayerPrefs.SetString(ClientIdPreference, clientId.Trim());
        PlayerPrefs.Save();
        Debug.Log("[Mao Gmail] event=client_configured connected=false");
        onResult?.Invoke("Đã lưu OAuth Client ID. Giờ Linh nói “Miku kết nối Gmail” nhé.");
    }

    private string BuildStatusReply()
    {
        if (!IsConfigured)
            return "Gmail chưa được cấu hình. Linh cần OAuth Client ID loại Desktop của Google trước nhé.";
        return IsConnected
            ? "Gmail đã kết nối ở chế độ chỉ đọc. Miku có thể kiểm tra mail mới hoặc tìm mail theo người gửi."
            : "Gmail đã có Client ID nhưng chưa kết nối. Linh nói “Miku kết nối Gmail” nhé.";
    }

    private IEnumerator ConnectRoutine(Action<string> onResult)
    {
        string clientId = GetClientId();
        if (!MaoGmailSecurity.IsValidDesktopClientId(clientId))
        {
            onResult?.Invoke("Gmail chưa có OAuth Client ID loại Desktop. Dùng /gmail client <client-id> để cấu hình trước nhé.");
            yield break;
        }

        operationInProgress = true;
        string verifier = MaoGmailOAuth.CreateCodeVerifier();
        string state = MaoGmailOAuth.CreateState();
        string challenge = MaoGmailOAuth.CreateCodeChallenge(verifier);
        Task<string> callbackTask = null;
        int port = 0;
        try
        {
            port = MaoWindowsLoopbackCallback.Start(out activeListener);
            IntPtr listenerForTask = activeListener;
            callbackTask = Task.Run(() => MaoWindowsLoopbackCallback.Receive(listenerForTask, port));
            string authorizationUrl = MaoGmailOAuth.BuildAuthorizationUrl(clientId, port, state, challenge);
            Process.Start(new ProcessStartInfo
            {
                FileName = authorizationUrl,
                UseShellExecute = true
            });
            Debug.Log("[Mao Gmail] event=oauth_browser_opened scope=readonly");
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[Mao Gmail] event=oauth_start_failed error_type=" + exception.GetType().Name);
            StopListener();
            operationInProgress = false;
            onResult?.Invoke("Miku không thể bắt đầu kết nối Gmail trên máy này.");
            yield break;
        }

        float deadline = Time.realtimeSinceStartup + OAuthTimeoutSeconds;
        while (!callbackTask.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
        StopListener();
        if (!callbackTask.IsCompleted)
        {
            callbackTask.ContinueWith(task =>
            {
                Exception ignored = task.Exception;
            }, TaskContinuationOptions.OnlyOnFaulted);
            operationInProgress = false;
            onResult?.Invoke("Kết nối Gmail đã hết thời gian chờ. Linh thử lại nhé.");
            yield break;
        }

        string callbackUrl;
        try { callbackUrl = callbackTask.Result; }
        catch (Exception exception)
        {
            Debug.LogWarning("[Mao Gmail] event=oauth_callback_failed error_type=" + exception.GetBaseException().GetType().Name);
            operationInProgress = false;
            onResult?.Invoke("Miku không nhận được phản hồi đăng nhập Gmail hợp lệ.");
            yield break;
        }

        if (!MaoGmailOAuth.TryParseCallback(callbackUrl, state, out string code, out string oauthError))
        {
            operationInProgress = false;
            Debug.Log("[Mao Gmail] event=oauth_denied has_error=" + (!string.IsNullOrWhiteSpace(oauthError)).ToString().ToLowerInvariant());
            onResult?.Invoke("Kết nối Gmail chưa được chấp nhận hoặc phản hồi không hợp lệ.");
            yield break;
        }

        string tokenJson = null;
        string tokenError = null;
        yield return PostFormRoutine(MaoGmailOAuth.TokenEndpoint, new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = "http://127.0.0.1:" + port + "/"
        }, (json, error) => { tokenJson = json; tokenError = error; });

        ZeroString(ref code);
        ZeroString(ref verifier);
        if (!string.IsNullOrWhiteSpace(tokenError))
        {
            operationInProgress = false;
            onResult?.Invoke("Google chưa cấp quyền Gmail. Linh kiểm tra Gmail API và OAuth Client ID nhé.");
            yield break;
        }

        try
        {
            MaoGmailTokenSet tokens = MaoGmailTokenResponseParser.Parse(tokenJson, true);
            ReplaceTokens(tokens);
            SaveCredential();
            Debug.Log("[Mao Gmail] event=connected scope=readonly");
            onResult?.Invoke("Gmail đã kết nối ở chế độ chỉ đọc rồi nè.");
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[Mao Gmail] event=token_validation_failed error_type=" + exception.GetType().Name);
            DeleteCredential();
            onResult?.Invoke("Phản hồi đăng nhập Gmail không hợp lệ nên Miku đã không lưu quyền truy cập.");
        }
        finally
        {
            ZeroString(ref tokenJson);
            operationInProgress = false;
        }
    }

    private IEnumerator DisconnectRoutine(Action<string> onResult)
    {
        operationInProgress = true;
        string tokenToRevoke = refreshToken;
        if (!string.IsNullOrWhiteSpace(tokenToRevoke))
        {
            yield return PostFormRoutine(MaoGmailOAuth.RevokeEndpoint,
                new Dictionary<string, string> { ["token"] = tokenToRevoke }, (_, __) => { });
        }
        DeleteCredential();
        operationInProgress = false;
        Debug.Log("[Mao Gmail] event=disconnected");
        onResult?.Invoke("Miku đã ngắt kết nối Gmail và xóa quyền lưu trên máy rồi.");
    }

    private IEnumerator ReadMessagesRoutine(string query, string heading, Action<string> onResult)
    {
        if (!IsConnected)
        {
            onResult?.Invoke(IsConfigured
                ? "Gmail chưa kết nối. Linh nói “Miku kết nối Gmail” trước nhé."
                : "Gmail chưa được cấu hình OAuth Client ID loại Desktop.");
            yield break;
        }

        operationInProgress = true;
        bool tokenReady = false;
        yield return EnsureAccessTokenRoutine(result => tokenReady = result);
        if (!tokenReady)
        {
            operationInProgress = false;
            onResult?.Invoke("Phiên Gmail đã hết hạn. Linh kết nối lại Gmail nhé.");
            yield break;
        }

        string listUrl = GmailApiBase + "?maxResults=" + MaximumMessages +
                         "&includeSpamTrash=false&q=" + Uri.EscapeDataString(query);
        string listJson = null;
        string listError = null;
        yield return GetAuthorizedRoutine(listUrl, (json, error) => { listJson = json; listError = error; });
        if (!string.IsNullOrWhiteSpace(listError))
        {
            operationInProgress = false;
            onResult?.Invoke("Miku chưa đọc được Gmail lúc này. Linh thử lại sau một chút nhé.");
            yield break;
        }

        IReadOnlyList<string> ids;
        try
        {
            ids = MaoGmailResponseParser.ParseMessageIds(listJson, MaximumMessages);
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[Mao Gmail] event=response_validation_failed error_type=" + exception.GetType().Name);
            onResult?.Invoke("Dữ liệu Gmail trả về không hợp lệ nên Miku đã bỏ qua để an toàn.");
            operationInProgress = false;
            ZeroString(ref listJson);
            yield break;
        }

        var summaries = new List<MaoGmailMessageSummary>();
        foreach (string id in ids)
        {
            string detailJson = null;
            string detailError = null;
            string detailUrl = GmailApiBase + "/" + Uri.EscapeDataString(id) +
                               "?format=metadata&metadataHeaders=From&metadataHeaders=Subject";
            yield return GetAuthorizedRoutine(detailUrl,
                (json, error) => { detailJson = json; detailError = error; });
            if (!string.IsNullOrWhiteSpace(detailError)) continue;
            try { summaries.Add(MaoGmailResponseParser.ParseMessage(detailJson)); }
            catch (Exception exception)
            {
                Debug.LogWarning("[Mao Gmail] event=message_validation_failed error_type=" + exception.GetType().Name);
            }
            finally { ZeroString(ref detailJson); }
        }
        Debug.Log("[Mao Gmail] event=messages_read count=" + summaries.Count + " scope=readonly");
        onResult?.Invoke(MaoGmailFormatter.Format(summaries, heading));
        operationInProgress = false;
        ZeroString(ref listJson);
    }

    private IEnumerator EnsureAccessTokenRoutine(Action<bool> onComplete)
    {
        if (!string.IsNullOrWhiteSpace(accessToken) && accessTokenExpiresUtc > DateTime.UtcNow.AddMinutes(1))
        {
            onComplete(true);
            yield break;
        }
        if (string.IsNullOrWhiteSpace(refreshToken) || !IsConfigured)
        {
            onComplete(false);
            yield break;
        }

        string tokenJson = null;
        string tokenError = null;
        yield return PostFormRoutine(MaoGmailOAuth.TokenEndpoint, new Dictionary<string, string>
        {
            ["client_id"] = GetClientId(),
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token"
        }, (json, error) => { tokenJson = json; tokenError = error; });
        if (!string.IsNullOrWhiteSpace(tokenError))
        {
            ClearAccessToken();
            onComplete(false);
            yield break;
        }
        try
        {
            MaoGmailTokenSet tokens = MaoGmailTokenResponseParser.Parse(tokenJson, false);
            ReplaceAccessToken(tokens.AccessToken, tokens.ExpiresInSeconds);
            onComplete(true);
        }
        catch
        {
            ClearAccessToken();
            onComplete(false);
        }
        finally { ZeroString(ref tokenJson); }
    }

    private IEnumerator GetAuthorizedRoutine(string url, Action<string, string> onComplete)
    {
        if (!MaoGmailSecurity.IsAllowedMessagesApiUrl(url))
        {
            onComplete(null, "blocked_url");
            yield break;
        }
        using (UnityWebRequest request = UnityWebRequest.Get(url))
        {
            request.timeout = RequestTimeoutSeconds;
            request.SetRequestHeader("Authorization", "Bearer " + accessToken);
            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning("[Mao Gmail] event=api_failed status=" + request.responseCode);
                onComplete(null, "request_failed");
                yield break;
            }
            string text = request.downloadHandler.text;
            if (text.Length > MaoGmailResponseParser.MaximumResponseCharacters)
            {
                onComplete(null, "response_too_large");
                yield break;
            }
            onComplete(text, null);
        }
    }

    private static IEnumerator PostFormRoutine(string endpoint, IDictionary<string, string> fields,
        Action<string, string> onComplete)
    {
        if (endpoint != MaoGmailOAuth.TokenEndpoint && endpoint != MaoGmailOAuth.RevokeEndpoint)
        {
            onComplete(null, "blocked_endpoint");
            yield break;
        }
        string body = string.Join("&", fields.Select(pair =>
            Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value ?? string.Empty)));
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        using (var request = new UnityWebRequest(endpoint, UnityWebRequest.kHttpVerbPOST))
        {
            request.uploadHandler = new UploadHandlerRaw(bytes);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/x-www-form-urlencoded");
            request.timeout = RequestTimeoutSeconds;
            yield return request.SendWebRequest();
            Array.Clear(bytes, 0, bytes.Length);
            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning("[Mao Gmail] event=oauth_http_failed status=" + request.responseCode);
                onComplete(null, "request_failed");
                yield break;
            }
            string text = request.downloadHandler.text;
            if (text.Length > 32 * 1024)
            {
                onComplete(null, "response_too_large");
                yield break;
            }
            onComplete(text, null);
        }
    }

    private string GetClientId()
    {
        string environmentValue = Environment.GetEnvironmentVariable(ClientIdEnvironment);
        if (MaoGmailSecurity.IsValidDesktopClientId(environmentValue)) return environmentValue.Trim();
        string saved = PlayerPrefs.GetString(ClientIdPreference, string.Empty);
        return MaoGmailSecurity.IsValidDesktopClientId(saved) ? saved.Trim() : string.Empty;
    }

    private void ReplaceTokens(MaoGmailTokenSet tokens)
    {
        ZeroString(ref refreshToken);
        refreshToken = tokens.RefreshToken;
        ReplaceAccessToken(tokens.AccessToken, tokens.ExpiresInSeconds);
    }

    private void ReplaceAccessToken(string token, int expiresInSeconds)
    {
        ClearAccessToken();
        accessToken = token ?? string.Empty;
        accessTokenExpiresUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, expiresInSeconds));
    }

    private void ClearAccessToken()
    {
        ZeroString(ref accessToken);
        accessTokenExpiresUtc = DateTime.MinValue;
    }

    private void SaveCredential()
    {
        byte[] plainBytes = Encoding.UTF8.GetBytes(refreshToken);
        try
        {
            byte[] protectedBytes = MaoWindowsDataProtector.Protect(plainBytes);
            var payload = new GmailCredentialFile
            {
                version = 1,
                encryptedRefreshToken = Convert.ToBase64String(protectedBytes)
            };
            File.WriteAllText(CredentialPath, JsonConvert.SerializeObject(payload));
            Array.Clear(protectedBytes, 0, protectedBytes.Length);
        }
        finally { Array.Clear(plainBytes, 0, plainBytes.Length); }
    }

    private void LoadCredential()
    {
        if (!File.Exists(CredentialPath)) return;
        try
        {
            var file = new FileInfo(CredentialPath);
            if (file.Length <= 0 || file.Length > 64 * 1024) throw new FormatException();
            GmailCredentialFile payload = JsonConvert.DeserializeObject<GmailCredentialFile>(File.ReadAllText(CredentialPath));
            if (payload == null || payload.version != 1 || string.IsNullOrWhiteSpace(payload.encryptedRefreshToken))
                throw new FormatException();
            byte[] protectedBytes = Convert.FromBase64String(payload.encryptedRefreshToken);
            byte[] plainBytes = MaoWindowsDataProtector.Unprotect(protectedBytes);
            try
            {
                string loaded = Encoding.UTF8.GetString(plainBytes);
                if (loaded.Length == 0 || loaded.Length > 8192 || loaded.Any(character => character < 0x21 || character > 0x7e))
                    throw new FormatException();
                refreshToken = loaded;
            }
            finally
            {
                Array.Clear(protectedBytes, 0, protectedBytes.Length);
                Array.Clear(plainBytes, 0, plainBytes.Length);
            }
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[Mao Gmail] event=credential_load_failed error_type=" + exception.GetType().Name);
            DeleteCredential();
        }
    }

    private void DeleteCredential()
    {
        ClearAccessToken();
        ZeroString(ref refreshToken);
        try { if (File.Exists(CredentialPath)) File.Delete(CredentialPath); }
        catch (Exception exception)
        {
            Debug.LogWarning("[Mao Gmail] event=credential_delete_failed error_type=" + exception.GetType().Name);
        }
    }

    private void StopListener()
    {
        if (activeListener == MaoWindowsLoopbackCallback.InvalidSocket) return;
        MaoWindowsLoopbackCallback.Stop(activeListener);
        activeListener = MaoWindowsLoopbackCallback.InvalidSocket;
    }

    private static void ZeroString(ref string value)
    {
        value = string.Empty;
    }

    [Serializable]
    private sealed class GmailCredentialFile
    {
        public int version;
        public string encryptedRefreshToken;
    }
}

internal static class MaoWindowsLoopbackCallback
{
    public static readonly IntPtr InvalidSocket = new IntPtr(-1);
    private const int AddressFamilyInternet = 2;
    private const int SocketStream = 1;
    private const int ProtocolTcp = 6;
    private const int SocketError = -1;
    private const int SocketLevel = 0xffff;
    private const int ReceiveTimeout = 0x1006;
    private const int SendTimeout = 0x1005;

    public static int Start(out IntPtr listener)
    {
        listener = InvalidSocket;
        if (WSAStartup(0x0202, out WsaData _) != 0)
            throw new InvalidOperationException("Windows loopback networking is unavailable.");
        try
        {
            listener = socket(AddressFamilyInternet, SocketStream, ProtocolTcp);
            if (listener == InvalidSocket) throw SocketFailure();
            int timeoutMilliseconds = 10000;
            setsockopt(listener, SocketLevel, ReceiveTimeout, ref timeoutMilliseconds, sizeof(int));
            setsockopt(listener, SocketLevel, SendTimeout, ref timeoutMilliseconds, sizeof(int));

            var address = new SockAddrIn
            {
                Family = AddressFamilyInternet,
                Port = 0,
                Address = 0x0100007f,
                Zero = new byte[8]
            };
            if (bind(listener, ref address, Marshal.SizeOf(typeof(SockAddrIn))) == SocketError ||
                listen(listener, 1) == SocketError)
                throw SocketFailure();
            int addressLength = Marshal.SizeOf(typeof(SockAddrIn));
            if (getsockname(listener, ref address, ref addressLength) == SocketError) throw SocketFailure();
            return SwapBytes(address.Port);
        }
        catch
        {
            Stop(listener);
            listener = InvalidSocket;
            throw;
        }
    }

    public static string Receive(IntPtr listener, int port)
    {
        IntPtr client = InvalidSocket;
        try
        {
            client = accept(listener, IntPtr.Zero, IntPtr.Zero);
            if (client == InvalidSocket) throw SocketFailure();
            string header = ReceiveHeader(client);
            string firstLine = header.Split(new[] { "\r\n" }, StringSplitOptions.None)[0];
            string[] parts = firstLine.Split(' ');
            if (parts.Length != 3 || parts[0] != "GET" || parts[1].Length > 8192 ||
                !parts[1].StartsWith("/?", StringComparison.Ordinal))
                throw new InvalidDataException("Invalid OAuth callback request.");

            byte[] page = Encoding.UTF8.GetBytes(
                "<!doctype html><meta charset=\"utf-8\"><title>Miku Gmail</title>" +
                "<p>Đã nhận phản hồi Gmail. Bạn có thể đóng tab này và quay lại với Miku.</p>");
            byte[] response = Encoding.UTF8.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\n" +
                "X-Content-Type-Options: nosniff\r\nCache-Control: no-store\r\n" +
                "Content-Length: " + page.Length + "\r\nConnection: close\r\n\r\n" +
                Encoding.UTF8.GetString(page));
            SendAll(client, response);
            Array.Clear(page, 0, page.Length);
            Array.Clear(response, 0, response.Length);
            return "http://127.0.0.1:" + port + parts[1];
        }
        finally
        {
            Close(client);
        }
    }

    public static bool SmokeRoundTrip()
    {
        IntPtr listener = InvalidSocket;
        IntPtr client = InvalidSocket;
        Task<string> receiveTask = null;
        try
        {
            int port = Start(out listener);
            IntPtr listenerForTask = listener;
            receiveTask = Task.Run(() => Receive(listenerForTask, port));
            client = socket(AddressFamilyInternet, SocketStream, ProtocolTcp);
            if (client == InvalidSocket) return false;
            var address = new SockAddrIn
            {
                Family = AddressFamilyInternet,
                Port = SwapBytes((ushort)port),
                Address = 0x0100007f,
                Zero = new byte[8]
            };
            if (connect(client, ref address, Marshal.SizeOf(typeof(SockAddrIn))) == SocketError) return false;
            byte[] request = Encoding.ASCII.GetBytes(
                "GET /?code=smoke-code&state=smoke-state HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n");
            SendAll(client, request);
            Array.Clear(request, 0, request.Length);
            if (!receiveTask.Wait(5000)) return false;
            return receiveTask.Result == "http://127.0.0.1:" + port + "/?code=smoke-code&state=smoke-state";
        }
        catch
        {
            return false;
        }
        finally
        {
            Close(client);
            Stop(listener);
            if (receiveTask != null && receiveTask.IsFaulted)
            {
                Exception ignored = receiveTask.Exception;
            }
        }
    }

    public static void Close(IntPtr socketHandle)
    {
        if (socketHandle != IntPtr.Zero && socketHandle != InvalidSocket) closesocket(socketHandle);
    }

    public static void Stop(IntPtr listener)
    {
        Close(listener);
        WSACleanup();
    }

    private static string ReceiveHeader(IntPtr client)
    {
        var bytes = new List<byte>(1024);
        var buffer = new byte[2048];
        int matched = 0;
        while (bytes.Count < 16384)
        {
            int read = recv(client, buffer, buffer.Length, 0);
            if (read <= 0) break;
            for (int index = 0; index < read; index++)
            {
                byte value = buffer[index];
                bytes.Add(value);
                byte expected = matched == 0 || matched == 2 ? (byte)'\r' : (byte)'\n';
                if (value == expected) matched++; else matched = value == '\r' ? 1 : 0;
                if (matched == 4) return Encoding.ASCII.GetString(bytes.ToArray());
                if (bytes.Count == 16384) break;
            }
        }
        throw new InvalidDataException("OAuth callback header is invalid or too large.");
    }

    private static void SendAll(IntPtr client, byte[] value)
    {
        int offset = 0;
        while (offset < value.Length)
        {
            byte[] remaining = offset == 0 ? value : value.Skip(offset).ToArray();
            int written = send(client, remaining, remaining.Length, 0);
            if (offset != 0) Array.Clear(remaining, 0, remaining.Length);
            if (written <= 0) throw SocketFailure();
            offset += written;
        }
    }

    private static ushort SwapBytes(ushort value)
    {
        return (ushort)((value >> 8) | (value << 8));
    }

    private static InvalidOperationException SocketFailure()
    {
        return new InvalidOperationException("Windows loopback networking failed: " + WSAGetLastError());
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct WsaData
    {
        public ushort Version;
        public ushort HighVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string Description;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 129)] public string SystemStatus;
        public ushort MaximumSockets;
        public ushort MaximumDatagramSize;
        public IntPtr VendorInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SockAddrIn
    {
        public short Family;
        public ushort Port;
        public uint Address;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] Zero;
    }

    [DllImport("Ws2_32.dll", SetLastError = true)]
    private static extern int WSAStartup(ushort version, out WsaData data);
    [DllImport("Ws2_32.dll")]
    private static extern int WSACleanup();
    [DllImport("Ws2_32.dll")]
    private static extern int WSAGetLastError();
    [DllImport("Ws2_32.dll", SetLastError = true)]
    private static extern IntPtr socket(int addressFamily, int socketType, int protocol);
    [DllImport("Ws2_32.dll", SetLastError = true)]
    private static extern int bind(IntPtr socketHandle, ref SockAddrIn address, int addressLength);
    [DllImport("Ws2_32.dll", SetLastError = true)]
    private static extern int listen(IntPtr socketHandle, int backlog);
    [DllImport("Ws2_32.dll", SetLastError = true)]
    private static extern int connect(IntPtr socketHandle, ref SockAddrIn address, int addressLength);
    [DllImport("Ws2_32.dll", SetLastError = true)]
    private static extern int getsockname(IntPtr socketHandle, ref SockAddrIn address, ref int addressLength);
    [DllImport("Ws2_32.dll", SetLastError = true)]
    private static extern IntPtr accept(IntPtr socketHandle, IntPtr address, IntPtr addressLength);
    [DllImport("Ws2_32.dll", SetLastError = true)]
    private static extern int recv(IntPtr socketHandle, byte[] buffer, int length, int flags);
    [DllImport("Ws2_32.dll", SetLastError = true)]
    private static extern int send(IntPtr socketHandle, byte[] buffer, int length, int flags);
    [DllImport("Ws2_32.dll", SetLastError = true)]
    private static extern int setsockopt(IntPtr socketHandle, int level, int optionName, ref int optionValue, int optionLength);
    [DllImport("Ws2_32.dll")]
    private static extern int closesocket(IntPtr socketHandle);
}

internal static class MaoWindowsDataProtector
{
    private const uint UiForbidden = 0x1;

    public static byte[] Protect(byte[] value) => Transform(value, true);
    public static byte[] Unprotect(byte[] value) => Transform(value, false);

    private static byte[] Transform(byte[] value, bool protect)
    {
        if (value == null || value.Length == 0) throw new ArgumentException("Credential is empty.", nameof(value));
        DataBlob input = CreateBlob(value);
        DataBlob output = default;
        try
        {
            bool success = protect
                ? CryptProtectData(ref input, "Mao Gmail credential", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    UiForbidden, ref output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    UiForbidden, ref output);
            if (!success || output.Data == IntPtr.Zero || output.Size <= 0)
                throw new InvalidOperationException("Windows credential protection failed: " + Marshal.GetLastWin32Error());
            byte[] result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally
        {
            if (input.Data != IntPtr.Zero)
            {
                for (int index = 0; index < input.Size; index++) Marshal.WriteByte(input.Data, index, 0);
                Marshal.FreeHGlobal(input.Data);
            }
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
        }
    }

    private static DataBlob CreateBlob(byte[] value)
    {
        var blob = new DataBlob { Size = value.Length, Data = Marshal.AllocHGlobal(value.Length) };
        Marshal.Copy(value, 0, blob.Data, value.Length);
        return blob;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("Crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob input, string description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, uint flags, ref DataBlob output);

    [DllImport("Crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, uint flags, ref DataBlob output);

    [DllImport("Kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
