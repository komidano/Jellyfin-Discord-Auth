using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Discord.WebSocket;
using Jellyfin.Database.Implementations.Entities;
using JellyfinDiscordAuth.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model;
using MediaBrowser.Model.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace JellyfinDiscordAuth.Services
{
    /// <summary>
    /// Encapsulates the Discord OAuth2 flow, state validation, and temporary session management.
    /// </summary>
    public class DiscordOAuthService
    {
        private readonly IUserManager _userManager;
        private readonly ISessionManager _sessionManager;
        private readonly ICryptoProvider _cryptoProvider;
        private readonly IProviderManager _providerManager;
        private readonly IServerConfigurationManager _serverConfigurationManager;
        private readonly DiscordBotService _discordBotService;
        private readonly DiscordUserSyncService _syncService;
        private readonly DiscordAuthSessionService _sessionService;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<DiscordOAuthService> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="DiscordOAuthService"/> class.
        /// </summary>
        public DiscordOAuthService(
            IUserManager userManager,
            ISessionManager sessionManager,
            ICryptoProvider cryptoProvider,
            IProviderManager providerManager,
            IServerConfigurationManager serverConfigurationManager,
            DiscordBotService discordBotService,
            DiscordUserSyncService syncService,
            DiscordAuthSessionService sessionService,
            IHttpClientFactory httpClientFactory,
            ILogger<DiscordOAuthService> logger)
        {
            _userManager = userManager;
            _sessionManager = sessionManager;
            _cryptoProvider = cryptoProvider;
            _providerManager = providerManager;
            _serverConfigurationManager = serverConfigurationManager;
            _discordBotService = discordBotService;
            _syncService = syncService;
            _sessionService = sessionService;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        private PluginConfiguration Config =>
            DiscordAuthPlugin.Instance?.Configuration
            ?? throw new InvalidOperationException("Plugin configuration is not available.");

        /// <summary>
        /// Gets the base URL for redirect URIs. Uses the configured ServerUrl if set,
        /// otherwise falls back to the request scheme and host.
        /// This is required when Jellyfin is behind a reverse proxy (e.g. Caddy, nginx)
        /// where the internal request uses HTTP but the public URL uses HTTPS.
        /// </summary>
        private string GetBaseUrl(HttpRequest request)
        {
            var serverUrl = Config.ServerUrl?.Trim().TrimEnd('/');
            if (!string.IsNullOrEmpty(serverUrl))
            {
                return serverUrl;
            }

            return $"{request.Scheme}://{request.Host}";
        }

        /// <summary>
        /// Generates the Discord OAuth2 authorize URL and stores a CSRF state cookie.
        /// </summary>
        /// <param name="httpContext">The HTTP context.</param>
        /// <returns>The authorize URL.</returns>
        public string BuildAuthorizeUrl(HttpContext httpContext)
        {
            var state = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            httpContext.Response.Cookies.Delete("discord_link_state");
            AppendStateCookie(httpContext, "discord_auth_state", state);

            return BuildDiscordAuthorizeUrl(httpContext.Request, state);
        }

        /// <summary>
        /// Creates a Discord OAuth URL for linking the authenticated Jellyfin account.
        /// </summary>
        /// <param name="httpContext">The HTTP context.</param>
        /// <param name="userId">The Jellyfin user id.</param>
        /// <returns>The Discord authorize URL.</returns>
        public string BuildAccountLinkAuthorizeUrl(HttpContext httpContext, Guid userId)
        {
            var state = _sessionService.CreateAccountLinkSession(userId);
            httpContext.Response.Cookies.Delete("discord_auth_state");
            AppendStateCookie(httpContext, "discord_link_state", state);

            return BuildDiscordAuthorizeUrl(httpContext.Request, state);
        }

        /// <summary>
        /// Validates the OAuth state cookie and completes the authentication flow.
        /// Returns a temporary exchange code for the frontend.
        /// </summary>
        /// <param name="httpContext">The HTTP context.</param>
        /// <param name="code">The Discord authorization code.</param>
        /// <param name="state">The Discord state parameter.</param>
        /// <returns>A tuple containing (success, exchangeCodeOrErrorMessage, isError).</returns>
        public async Task<(bool Success, string Result, bool IsError)> ProcessCallbackAsync(HttpContext httpContext, string code, string? state)
        {
            // Verify CSRF state
            if (!httpContext.Request.Cookies.TryGetValue("discord_auth_state", out var expectedState)
                || string.IsNullOrEmpty(expectedState)
                || !string.Equals(expectedState, state, StringComparison.Ordinal))
            {
                _logger.LogWarning("Invalid or missing OAuth state parameter. Possible CSRF attack.");
                return (false, "Invalid or expired session. Please try logging in again.", true);
            }

            // Clear the state cookie
            httpContext.Response.Cookies.Delete("discord_auth_state");

            var redirectUri = $"{GetBaseUrl(httpContext.Request)}/DiscordAuth/Callback";
            using var httpClient = _httpClientFactory.CreateClient();
            var identity = await GetDiscordIdentityAsync(httpClient, code, redirectUri).ConfigureAwait(false);
            if (identity.UserData == null)
            {
                return (false, identity.Error ?? "Failed to retrieve Discord user information.", true);
            }

            var userData = identity.UserData;
            var discordUser = identity.GuildUser;

            string username = userData.Global_name ?? userData.Username;
            _logger.LogInformation("Discord user data received for {Username}.", username);

            // Match or create a Jellyfin user
            RemoveOrphanedDiscordLinks(userData.Id);
            var savedDiscordUser = Config.DiscordUserData?.FirstOrDefault(x => x.Value.Id == userData.Id) ?? default;
            User? user = savedDiscordUser.Key == Guid.Empty
                ? null
                : _userManager.GetUserById(savedDiscordUser.Key);

            if (savedDiscordUser.Key != Guid.Empty && user == null)
            {
                RemoveOrphanedDiscordLinks(userData.Id);
                savedDiscordUser = default;
            }

            if (user == null)
            {
                try
                {
                    _logger.LogInformation("SSO user {Username} doesn't exist, creating...", username);
                    user = await _userManager.CreateUserAsync(username).ConfigureAwait(false);
                    user.AuthenticationProviderId = typeof(DiscordAuthenticationProvider).FullName!;
                    user.Password = _cryptoProvider.CreatePasswordHash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(64))).ToString();
                    await _userManager.UpdateUserAsync(user).ConfigureAwait(false);

                    savedDiscordUser = new KeyValuePair<Guid, DiscordUser>(user.Id, userData);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error creating user {Username}", username);
                    return (false, "Unable to create a Jellyfin account. If you already have an account, sign in to it and link Discord from your profile.", true);
                }
            }
            else
            {
                _logger.LogInformation("Discord user {Username} found in config, updating linked account data...", username);
                savedDiscordUser = new KeyValuePair<Guid, DiscordUser>(user.Id, userData);
            }

            // Save Discord avatar
            try
            {
                await SaveDiscordAvatarAsync(httpClient, user, userData).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save Discord avatar");
            }

            // Update DiscordUserData in config
            Config.DiscordUserData![savedDiscordUser.Key] = savedDiscordUser.Value;
            DiscordAuthPlugin.Instance?.SaveConfiguration();

            // Apply folder access only after the explicit Discord-to-Jellyfin mapping is saved.
            if (discordUser != null)
            {
                await _syncService.ApplyDiscordRolesAsync(discordUser).ConfigureAwait(false);
            }

            // Authenticate the user in Jellyfin
            var authRequest = new AuthenticationRequest
            {
                UserId = user.Id,
                Username = user.Username,
                App = "Discord",
                AppVersion = "1.0.0.0",
                DeviceId = Guid.NewGuid().ToString(),
                DeviceName = "Discord OAuth2"
            };

            var authenticationResult = await _sessionManager.AuthenticateDirect(authRequest).ConfigureAwait(false);
            _logger.LogInformation("Discord auth successful for user {Username}.", username);

            // Create a temporary session instead of exposing the token in HTML
            var exchangeCode = _sessionService.CreateSession(
                authenticationResult.AccessToken,
                authenticationResult.User.Id,
                authenticationResult.User.ServerId,
                authenticationResult.User.Name);

            return (true, exchangeCode, false);
        }

        /// <summary>
        /// Gets the Discord link status for a Jellyfin user.
        /// </summary>
        /// <param name="userId">The Jellyfin user id.</param>
        /// <returns>The link status, or null if the Jellyfin user does not exist.</returns>
        public (Guid UserId, bool IsLinked, string? DiscordUsername, bool CanUnlink)? GetAccountLinkStatus(Guid userId)
        {
            var user = _userManager.GetUserById(userId);
            if (user == null)
            {
                return null;
            }

            DiscordUser? discordUser = null;
            var isLinked = Config.DiscordUserData != null && Config.DiscordUserData.TryGetValue(userId, out discordUser);
            var canUnlink = isLinked
                && !string.Equals(user.AuthenticationProviderId, typeof(DiscordAuthenticationProvider).FullName, StringComparison.Ordinal);

            return (userId, isLinked, discordUser?.Global_name ?? discordUser?.Username, canUnlink);
        }

        /// <summary>
        /// Removes a Jellyfin user's Discord association when another sign-in method is available.
        /// </summary>
        /// <param name="userId">The Jellyfin user id.</param>
        /// <returns>Whether the association was removed, and an error message if it was not.</returns>
        public (bool Success, string? Error) UnlinkDiscordAccount(Guid userId)
        {
            var user = _userManager.GetUserById(userId);
            if (user == null)
            {
                return (false, "Jellyfin user not found.");
            }

            var discordUserData = Config.DiscordUserData;
            if (discordUserData == null || !discordUserData.ContainsKey(userId))
            {
                return (false, "No Discord account is linked to this Jellyfin account.");
            }

            if (string.Equals(user.AuthenticationProviderId, typeof(DiscordAuthenticationProvider).FullName, StringComparison.Ordinal))
            {
                return (false, "Add another sign-in method before unlinking this Discord-only account.");
            }

            discordUserData.Remove(userId);
            DiscordAuthPlugin.Instance?.SaveConfiguration();
            return (true, null);
        }

        /// <summary>
        /// Completes an OAuth callback for linking a Discord account to a Jellyfin user.
        /// </summary>
        /// <param name="httpContext">The HTTP context.</param>
        /// <param name="code">The Discord authorization code.</param>
        /// <param name="state">The Discord state parameter.</param>
        /// <returns>A tuple containing success, the Jellyfin user id or error message, and the error flag.</returns>
        public async Task<(bool Success, string Result, bool IsError)> ProcessAccountLinkCallbackAsync(HttpContext httpContext, string code, string? state)
        {
            if (!httpContext.Request.Cookies.TryGetValue("discord_link_state", out var expectedState)
                || string.IsNullOrEmpty(expectedState)
                || !string.Equals(expectedState, state, StringComparison.Ordinal))
            {
                httpContext.Response.Cookies.Delete("discord_link_state");
                _logger.LogWarning("Invalid or missing Discord account link state.");
                return (false, "Invalid or expired link request. Please try again.", true);
            }

            httpContext.Response.Cookies.Delete("discord_link_state");
            var userId = _sessionService.RetrieveAndRemoveAccountLinkSession(expectedState);
            if (userId == null)
            {
                return (false, "This Discord account link request has expired. Please try again.", true);
            }

            var user = _userManager.GetUserById(userId.Value);
            if (user == null)
            {
                return (false, "The Jellyfin account no longer exists.", true);
            }

            var redirectUri = $"{GetBaseUrl(httpContext.Request)}/DiscordAuth/Callback";
            using var httpClient = _httpClientFactory.CreateClient();
            var identity = await GetDiscordIdentityAsync(httpClient, code, redirectUri).ConfigureAwait(false);
            var userData = identity.UserData;
            if (userData == null)
            {
                return (false, identity.Error ?? "Failed to retrieve Discord user information.", true);
            }

            RemoveOrphanedDiscordLinks(userData.Id);
            var existingDiscordLink = Config.DiscordUserData!.FirstOrDefault(
                pair => string.Equals(pair.Value.Id, userData.Id, StringComparison.Ordinal));
            if (existingDiscordLink.Key != Guid.Empty && existingDiscordLink.Key != userId.Value)
            {
                return (false, "This Discord account is already linked to another Jellyfin account.", true);
            }

            if (Config.DiscordUserData.TryGetValue(userId.Value, out var currentDiscordLink)
                && !string.Equals(currentDiscordLink.Id, userData.Id, StringComparison.Ordinal))
            {
                return (false, "This Jellyfin account is already linked to a different Discord account.", true);
            }

            Config.DiscordUserData[userId.Value] = userData;
            DiscordAuthPlugin.Instance?.SaveConfiguration();

            if (identity.GuildUser != null)
            {
                await _syncService.ApplyDiscordRolesAsync(identity.GuildUser).ConfigureAwait(false);
            }

            _logger.LogInformation("Linked Discord account to Jellyfin user {UserId}.", userId.Value);
            return (true, userId.Value.ToString(), false);
        }

        private void RemoveOrphanedDiscordLinks(string discordUserId)
        {
            var discordUserData = Config.DiscordUserData;
            if (discordUserData == null)
            {
                return;
            }

            var orphanedUserIds = discordUserData
                .Where(pair => string.Equals(pair.Value.Id, discordUserId, StringComparison.Ordinal)
                    && _userManager.GetUserById(pair.Key) == null)
                .Select(pair => pair.Key)
                .ToArray();

            foreach (var userId in orphanedUserIds)
            {
                discordUserData.Remove(userId);
            }

            if (orphanedUserIds.Length > 0)
            {
                DiscordAuthPlugin.Instance?.SaveConfiguration();
                _logger.LogInformation(
                    "Removed {Count} stale Discord account link(s) for deleted Jellyfin user(s).",
                    orphanedUserIds.Length);
            }
        }

        /// <summary>
        /// Exchanges a temporary code for the real Jellyfin authentication data.
        /// </summary>
        /// <param name="code">The temporary exchange code.</param>
        /// <returns>The session data, or null if invalid/expired.</returns>
        public DiscordAuthSession? ExchangeCode(string code)
        {
            return _sessionService.RetrieveAndRemoveSession(code);
        }

        private string BuildDiscordAuthorizeUrl(HttpRequest request, string state)
        {
            var redirectUri = $"{GetBaseUrl(request)}/DiscordAuth/Callback";
            return $"https://discord.com/oauth2/authorize?response_type=code&client_id={Config.ClientId}&scope=identify%20email&redirect_uri={Uri.EscapeDataString(redirectUri)}&prompt=consent&state={Uri.EscapeDataString(state)}";
        }

        private void AppendStateCookie(HttpContext httpContext, string cookieName, string state)
        {
            var request = httpContext.Request;
            httpContext.Response.Cookies.Append(cookieName, state, new CookieOptions
            {
                HttpOnly = true,
                Secure = request.IsHttps || GetBaseUrl(request).StartsWith("https", StringComparison.OrdinalIgnoreCase),
                SameSite = SameSiteMode.Lax,
                MaxAge = TimeSpan.FromMinutes(10),
                Path = httpContext.Request.PathBase.Add(new PathString("/DiscordAuth")).Value
            });
        }

        private async Task<(DiscordUser? UserData, SocketGuildUser? GuildUser, string? Error)> GetDiscordIdentityAsync(
            HttpClient httpClient,
            string code,
            string redirectUri)
        {
            var tokenResponse = await httpClient.PostAsync(
                "https://discord.com/api/oauth2/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    { "client_id", Config.ClientId },
                    { "client_secret", Config.ClientSecret },
                    { "grant_type", "authorization_code" },
                    { "code", code },
                    { "redirect_uri", redirectUri }
                })).ConfigureAwait(false);

            var tokenJson = await tokenResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
            var tokenData = JsonConvert.DeserializeObject<Dictionary<string, object>>(tokenJson);
            string? accessToken = tokenData?.TryGetValue("access_token", out var tokenValue) == true
                ? tokenValue?.ToString()
                : null;

            if (!tokenResponse.IsSuccessStatusCode || string.IsNullOrEmpty(accessToken))
            {
                _logger.LogError("Failed to exchange Discord authorization code for access token.");
                return (null, null, "Failed to authenticate with Discord.");
            }

            httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            var userResponse = await httpClient.GetAsync("https://discord.com/api/users/@me").ConfigureAwait(false);
            var userJson = await userResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
            var userData = JsonConvert.DeserializeObject<DiscordUser>(userJson);
            if (!userResponse.IsSuccessStatusCode || userData == null || string.IsNullOrEmpty(userData.Id))
            {
                _logger.LogError("Failed to retrieve Discord user data.");
                return (null, null, "Failed to retrieve Discord user information.");
            }

            SocketGuildUser? discordUser = null;
            if (!string.IsNullOrWhiteSpace(Config.ServerId.Trim()))
            {
                try
                {
                    if (!ulong.TryParse(Config.ServerId.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsedServerId))
                    {
                        return (null, null, "Invalid Server ID configuration.");
                    }

                    if (!ulong.TryParse(userData.Id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsedUserId))
                    {
                        return (null, null, "Invalid Discord user ID.");
                    }

                    var server = _discordBotService.Client?.GetGuild(parsedServerId);
                    if (server == null)
                    {
                        return (null, null, "Discord server not found. Please check the Server ID configuration.");
                    }

                    await server.DownloadUsersAsync().ConfigureAwait(false);
                    discordUser = server.Users.FirstOrDefault(user => user.Id == parsedUserId);
                    if (discordUser == null)
                    {
                        return (null, null, "You must be a member of the server to access Jellyfin.");
                    }

                    if (!discordUser.Roles.Any(role => !role.IsEveryone))
                    {
                        return (null, null, "You must have a role other than @everyone to access Jellyfin.");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while fetching server users and roles.");
                    return (null, null, "Something went wrong while fetching server users and roles. Please try again later.");
                }
            }
            else
            {
                _logger.LogWarning("Server ID is not set in the plugin configuration, skipping role check.");
            }

            return (userData, discordUser, null);
        }

        private async Task SaveDiscordAvatarAsync(HttpClient httpClient, User user, DiscordUser userData)
        {
            var avatarUrl = $"https://cdn.discordapp.com/avatars/{userData.Id}/{userData.Avatar}.png";
            var avatarResponse = await httpClient.GetAsync(avatarUrl).ConfigureAwait(false);

            if (!avatarResponse.Content.Headers.TryGetValues("content-type", out var contentTypeList))
            {
                throw new InvalidOperationException("Cannot get Content-Type of image: " + avatarUrl);
            }

            var contentType = contentTypeList.First();
            if (!contentType.StartsWith("image", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Content type of avatar URL is not an image, got: " + contentType);
            }

            var extension = contentType.Split("/").Last();
            var stream = await avatarResponse.Content.ReadAsStreamAsync().ConfigureAwait(false);
            await using (stream)
            {
                var userDataPath = Path.Combine(_serverConfigurationManager.ApplicationPaths.UserConfigurationDirectoryPath, user.Username);
                if (user.ProfileImage is not null)
                {
                    await _userManager.ClearProfileImageAsync(user).ConfigureAwait(false);
                }

                user.ProfileImage = new ImageInfo(Path.Combine(userDataPath, "profile" + extension));
                await _providerManager.SaveImage(stream, contentType, user.ProfileImage.Path)
                    .ConfigureAwait(false);
                await _userManager.UpdateUserAsync(user).ConfigureAwait(false);
            }
        }
    }
}
