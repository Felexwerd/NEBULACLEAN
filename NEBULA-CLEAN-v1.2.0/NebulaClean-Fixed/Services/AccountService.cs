using System.IO;
using System.Net.Http.Json;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Util.Store;

namespace NebulaClean.Services;

public sealed class AccountService
{
    private static readonly HttpClient Http = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    public static AccountService Instance { get; } = new();
    public string? Token { get; private set; }
    public AccountInfo? CurrentUser { get; private set; }
    public string ApiBaseUrl { get; }
    public string AppVersion { get; }

    private AccountService()
    {
        var config = File.Exists("appsettings.json") ? JsonDocument.Parse(File.ReadAllText("appsettings.json")).RootElement : default;
        ApiBaseUrl = config.ValueKind == JsonValueKind.Object && config.TryGetProperty("ApiBaseUrl", out var url) ? url.GetString() ?? "" : "";
        AppVersion = config.ValueKind == JsonValueKind.Object && config.TryGetProperty("AppVersion", out var ver) ? ver.GetString() ?? "1.0.0" : "1.0.0";
    }

    public async Task<AuthResult> RegisterAsync(string email, string password) => await PostAuthAsync("api/auth/register", new { email, password });
    public async Task<AuthResult> LoginAsync(string email, string password) => await PostAuthAsync("api/auth/login", new { email, password });

    private async Task<AuthResult> PostAuthAsync(string path, object payload)
    {
        var result = await SendAsync<AuthResult>(HttpMethod.Post, path, payload, false);
        if (!string.IsNullOrWhiteSpace(result.Token)) { Token = result.Token; CurrentUser = result.User; }
        return result;
    }

    public async Task<AuthResult> GoogleLoginAsync()
    {
        string secretsPath = Path.Combine(AppContext.BaseDirectory, "client_secrets.json");
        if (!File.Exists(secretsPath)) throw new InvalidOperationException("Для входа через Google добавьте client_secrets.json из Google Cloud Console в папку приложения. См. README.");
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(secretsPath));
        var root = doc.RootElement;
        var secrets = root.TryGetProperty("installed", out var installed) ? installed : root.GetProperty("web");
        var clientSecrets = new ClientSecrets { ClientId = secrets.GetProperty("client_id").GetString(), ClientSecret = secrets.GetProperty("client_secret").GetString() };
        var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(clientSecrets, new[] { "openid", "email", "profile" }, "nebula-user", CancellationToken.None, new FileDataStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NebulaClean", "GoogleTokens")));
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(NormalizeBaseUrl()), "api/auth/google"));
        req.Content = JsonContent.Create(new { accessToken = credential.Token.AccessToken });
        using var response = await Http.SendAsync(req);
        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(ReadError(json));
        var result = JsonSerializer.Deserialize<AuthResult>(json, JsonOptions) ?? throw new InvalidOperationException("Сервер вернул пустой ответ.");
        Token = result.Token; CurrentUser = result.User;
        return result;
    }

    public async Task<ProfileInfo> GetProfileAsync() => await SendAsync<ProfileInfo>(HttpMethod.Get, "api/profile");
    public async Task SaveProfileAsync(string prefix, string avatar) => await SendAsync<object>(HttpMethod.Put, "api/profile", new { prefix, avatar });
    public async Task<List<ChatMessageInfo>> GetChatMessagesAsync() => await SendAsync<List<ChatMessageInfo>>(HttpMethod.Get, "api/chat/messages");
    public async Task SendChatMessageAsync(string content, string prefix, string avatar) => await SendChatMediaAsync(content, "text", prefix, avatar);
    public async Task SendChatMediaAsync(string content, string messageType, string prefix, string avatar) => await SendAsync<object>(HttpMethod.Post, "api/chat/messages", new { content, prefix, avatar, messageType });

    public async Task ChangePasswordAsync(string currentPassword, string newPassword) => await SendAsync<object>(HttpMethod.Post, "api/auth/change-password", new { currentPassword, newPassword });
    public async Task<SubscriptionInfo> GetSubscriptionAsync() => await SendAsync<SubscriptionInfo>(HttpMethod.Get, "api/subscription");
    public async Task<SubscriptionInfo> RedeemPromoAsync(string code) => await SendAsync<SubscriptionInfo>(HttpMethod.Post, "api/promo/redeem", new { code });
    public async Task<PromoCreateResult> CreatePromoAsync(string code, int days, int uses) => await SendAsync<PromoCreateResult>(HttpMethod.Post, "api/admin/promos", new { code, durationDays = days, maxUses = uses });
    public async Task UpdateVersionAsync(string latestVersion, string minimumVersion, string downloadUrl, string message) => await SendAsync<object>(HttpMethod.Post, "api/admin/version", new { latestVersion, minimumVersion, downloadUrl, message });
    public async Task<AppVersionInfo> GetAppVersionAsync() => await SendAsync<AppVersionInfo>(HttpMethod.Get, $"api/app-version?current={Uri.EscapeDataString(AppVersion)}", false);

    public void Logout() { Token = null; CurrentUser = null; }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? payload = null, bool auth = true)
    {
        using var req = new HttpRequestMessage(method, new Uri(new Uri(NormalizeBaseUrl()), path));
        if (payload != null) req.Content = JsonContent.Create(payload);
        if (auth && !string.IsNullOrEmpty(Token)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        using var response = await Http.SendAsync(req);
        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(ReadError(json));
        if (typeof(T) == typeof(object)) return (T)(object)new object();
        return JsonSerializer.Deserialize<T>(json, JsonOptions) ?? throw new InvalidOperationException("Сервер вернул пустой ответ.");
    }

    private string NormalizeBaseUrl()
    {
        if (string.IsNullOrWhiteSpace(ApiBaseUrl) || ApiBaseUrl.Contains("YOUR-NEBULA-SERVER")) throw new InvalidOperationException("Сначала укажите адрес вашего сервера в appsettings.json. Для общих аккаунтов и промокодов нужен единый сервер.");
        return ApiBaseUrl.EndsWith('/') ? ApiBaseUrl : ApiBaseUrl + "/";
    }
    private static string ReadError(string json) { try { using var d = JsonDocument.Parse(json); if (d.RootElement.TryGetProperty("error", out var e)) return e.GetString() ?? "Ошибка сервера"; if (d.RootElement.TryGetProperty("title", out var t)) return t.GetString() ?? "Ошибка сервера"; } catch { } return "Ошибка сервера: " + json; }
}

public sealed class AuthResult { public string? Token { get; set; } public AccountInfo? User { get; set; } public string? Error { get; set; } }
public sealed class AccountInfo { public string Email { get; set; } = ""; public string Role { get; set; } = "user"; }
public sealed class SubscriptionInfo { public string Plan { get; set; } = "Free"; public DateTime? ExpiresAtUtc { get; set; } public bool IsAdmin { get; set; } public string Message { get; set; } = ""; }
public sealed class PromoCreateResult { public string Code { get; set; } = ""; public DateTime ExpiresAtUtc { get; set; } }
public sealed class AppVersionInfo { public string LatestVersion { get; set; } = "1.0.0"; public string MinimumVersion { get; set; } = "1.0.0"; public string DownloadUrl { get; set; } = ""; public string Message { get; set; } = ""; public bool MustUpdate { get; set; } }

public sealed class ChatMessageInfo { public long Id { get; set; } public string Email { get; set; } = ""; public string Role { get; set; } = "user"; public string Prefix { get; set; } = ""; public string Avatar { get; set; } = ""; public string Content { get; set; } = ""; public string MessageType { get; set; } = "text"; public DateTime CreatedAtUtc { get; set; } public string DisplayPrefix => string.IsNullOrWhiteSpace(Prefix) ? "free" : Prefix; public string DisplayContent => MessageType == "image" ? "📷 Фотография — нажмите, чтобы открыть" : MessageType == "audio" ? "🎙 Голосовое сообщение — нажмите, чтобы прослушать" : Content; public override string ToString() => $"{(string.IsNullOrWhiteSpace(Avatar) ? "✦" : "◉")}  {(string.IsNullOrWhiteSpace(Prefix) ? "free" : Prefix)}  {Email}  ·  {CreatedAtUtc.ToLocalTime():HH:mm}\n{DisplayContent}"; }

public sealed class ProfileInfo { public string Email { get; set; } = ""; public string Prefix { get; set; } = ""; public string Avatar { get; set; } = ""; }
