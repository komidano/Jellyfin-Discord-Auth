using System;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Reflection;
using System.Threading.Tasks;
using JellyfinDiscordAuth.Services;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace JellyfinDiscordAuth.Api
{
    /// <summary>
    /// API controller for Discord OAuth2 authentication.
    /// </summary>
    [ApiController]
    [Route("DiscordAuth")]
    public class DiscordController : ControllerBase
    {
        private readonly ILibraryManager _libraryManager;
        private readonly DiscordBotService _discordBotService;
        private readonly DiscordOAuthService _oauthService;
        private readonly ILogger<DiscordController> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="DiscordController"/> class.
        /// </summary>
        public DiscordController(
            ILibraryManager libraryManager,
            DiscordBotService discordBotService,
            DiscordOAuthService oauthService,
            ILogger<DiscordController> logger)
        {
            _libraryManager = libraryManager;
            _discordBotService = discordBotService;
            _oauthService = oauthService;
            _logger = logger;
        }

        /// <summary>
        /// Gets library and role data for the configuration page.
        /// </summary>
        [HttpGet("ConfigurationData")]
        [Produces(MediaTypeNames.Application.Json)]
        public IActionResult GetConfigurationData()
        {
            var libraries = _libraryManager.GetVirtualFolders()
                .Select(v => new
                {
                    v.ItemId,
                    v.Name
                })
                .OrderBy(v => v.Name)
                .ToArray();

            var roles = Array.Empty<object>();
            var config = DiscordAuthPlugin.Instance?.Configuration;

            if (config != null
                && !string.IsNullOrWhiteSpace(config.ServerId)
                && ulong.TryParse(config.ServerId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out ulong serverId)
                && _discordBotService.Client != null)
            {
                var guild = _discordBotService.Client.GetGuild(serverId);
                if (guild != null)
                {
                    roles = guild.Roles
                        .Where(r => !r.IsEveryone)
                        .OrderByDescending(r => r.Position)
                        .Select(r => (object)new
                        {
                            Id = r.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            r.Name
                        })
                        .ToArray();
                }
            }

            return Ok(new
            {
                Libraries = libraries,
                Roles = roles
            });
        }

        /// <summary>
        /// Generates the Discord OAuth2 authorize URL and redirects the user to Discord.
        /// </summary>
        [HttpGet("Login")]
        public IActionResult Login()
        {
            var config = DiscordAuthPlugin.Instance?.Configuration;
            if (config == null || string.IsNullOrWhiteSpace(config.ClientId))
            {
                return Problem("Discord authentication is not configured.");
            }

            var authorizeUrl = _oauthService.BuildAuthorizeUrl(HttpContext);
            return Redirect(authorizeUrl);
        }

        /// <summary>
        /// Gets the Discord link status for the authenticated Jellyfin user.
        /// </summary>
        [Authorize]
        [HttpGet("AccountLink")]
        [Produces(MediaTypeNames.Application.Json)]
        public IActionResult GetAccountLinkStatus()
        {
            if (!TryGetAuthenticatedUserId(out var userId))
            {
                return Unauthorized();
            }

            var status = _oauthService.GetAccountLinkStatus(userId);
            if (status == null)
            {
                return NotFound();
            }

            return Ok(new
            {
                userId = status.Value.UserId,
                isLinked = status.Value.IsLinked,
                discordUsername = status.Value.DiscordUsername,
                canUnlink = status.Value.CanUnlink
            });
        }

        /// <summary>
        /// Starts linking a Discord account to the authenticated Jellyfin user.
        /// </summary>
        [Authorize]
        [HttpPost("AccountLink/Begin")]
        [Produces(MediaTypeNames.Application.Json)]
        public IActionResult BeginAccountLink()
        {
            if (!TryGetAuthenticatedUserId(out var userId))
            {
                return Unauthorized();
            }

            var config = DiscordAuthPlugin.Instance?.Configuration;
            if (config == null || string.IsNullOrWhiteSpace(config.ClientId))
            {
                return Problem("Discord authentication is not configured.");
            }

            return Ok(new { url = _oauthService.BuildAccountLinkAuthorizeUrl(HttpContext, userId) });
        }

        /// <summary>
        /// Removes the Discord link from the authenticated Jellyfin user.
        /// </summary>
        [Authorize]
        [HttpPost("AccountLink/Unlink")]
        [Produces(MediaTypeNames.Application.Json)]
        public IActionResult UnlinkAccount()
        {
            if (!TryGetAuthenticatedUserId(out var userId))
            {
                return Unauthorized();
            }

            var (success, error) = _oauthService.UnlinkDiscordAccount(userId);
            if (!success)
            {
                return BadRequest(new { message = error });
            }

            return Ok(new { success = true });
        }

        /// <summary>
        /// The Discord OAuth2 callback endpoint.
        /// </summary>
        [HttpGet("Callback")]
        [Produces(MediaTypeNames.Application.Json)]
        public async Task<IActionResult> Callback([FromQuery] string code, [FromQuery] string? state)
        {
            if (Request.Cookies.ContainsKey("discord_link_state"))
            {
                var (linkSuccess, linkResult, _) = await _oauthService.ProcessAccountLinkCallbackAsync(HttpContext, code, state).ConfigureAwait(false);
                if (!linkSuccess)
                {
                    return Content(HtmlError(linkResult), MediaTypeNames.Text.Html);
                }

                return Redirect($"{Request.PathBase}/web/index.html#!/userprofile?userId={Uri.EscapeDataString(linkResult)}");
            }

            var (success, result, _) = await _oauthService.ProcessCallbackAsync(HttpContext, code, state).ConfigureAwait(false);

            if (!success)
            {
                return Content(HtmlError(result), MediaTypeNames.Text.Html);
            }

            // Redirect to the completion page with the temporary exchange code
            return Redirect($"/DiscordAuth/Complete?code={Uri.EscapeDataString(result)}");
        }

        /// <summary>
        /// Serves the login script that injects the Discord button on the login page.
        /// </summary>
        [HttpGet("LoginScript")]
        public IActionResult GetLoginScript()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = "JellyfinDiscordAuth.Configuration.loginScript.js";
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                return NotFound();
            }

            using var reader = new StreamReader(stream);
            return Content(reader.ReadToEnd(), "application/javascript");
        }

        /// <summary>
        /// Serves the completion page that exchanges the temporary code for a real token.
        /// </summary>
        [HttpGet("Complete")]
        [Produces(MediaTypeNames.Text.Html)]
        public IActionResult Complete()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = "JellyfinDiscordAuth.Configuration.callbackPage.html";
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                _logger.LogError("Callback page resource not found: {ResourceName}", resourceName);
                return Content(HtmlError("Authentication page is missing."), MediaTypeNames.Text.Html);
            }

            using var reader = new StreamReader(stream);
            var html = reader.ReadToEnd();
            return Content(html, MediaTypeNames.Text.Html);
        }

        /// <summary>
        /// Exchanges a temporary code for Jellyfin authentication data.
        /// </summary>
        [HttpPost("Exchange")]
        [Produces(MediaTypeNames.Application.Json)]
        public IActionResult Exchange([FromBody] ExchangeRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.Code))
            {
                return BadRequest(new { message = "Exchange code is required." });
            }

            var session = _oauthService.ExchangeCode(request.Code);
            if (session == null)
            {
                return BadRequest(new { message = "Invalid or expired exchange code." });
            }

            return Ok(new
            {
                accessToken = session.AccessToken,
                userId = session.UserId.ToString(),
                serverId = session.ServerId,
                username = session.Username
            });
        }

        private bool TryGetAuthenticatedUserId(out Guid userId)
        {
            return Guid.TryParse(User.FindFirst("Jellyfin-UserId")?.Value, out userId);
        }

        private static string HtmlError(string message)
        {
            var encoded = System.Net.WebUtility.HtmlEncode(message);
            return $@"
<html>
<head>
<title>Error</title>
<script>
window.onload = function () {{
    alert(document.getElementById('err').textContent);
    window.location.href = '/web/index.html';
}};
</script>
</head>
<body>
<h1>Error</h1>
<p id=""err"">{encoded}</p>
</body>
</html>";
        }
    }

    /// <summary>
    /// Request model for the exchange endpoint.
    /// </summary>
    public class ExchangeRequest
    {
        /// <summary>
        /// Gets or sets the temporary exchange code.
        /// </summary>
        public string? Code { get; set; }
    }
}
