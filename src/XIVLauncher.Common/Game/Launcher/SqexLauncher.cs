

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;

#if NET6_0_OR_GREATER && !WIN32
using System.Net.Security;
#endif

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Serilog;
using XIVLauncher.Common.Game.Patch.PatchList;
using XIVLauncher.Common.Encryption;
using XIVLauncher.Common.Game.Exceptions;
using XIVLauncher.Common.PlatformAbstractions;
using XIVLauncher.Common.Util;
using System.Diagnostics;

#nullable enable

namespace XIVLauncher.Common.Game.Launcher;

public class SqexLauncher : ILauncher
{
    private readonly IUniqueIdCache uniqueIdCache;
    private readonly ISettings settings;
    private readonly HttpClient client;
    private OauthLoginResult? oauthLoginResult;
    private string? uniqueId;

    public SqexLauncher(IUniqueIdCache uniqueIdCache, ISettings settings)
    {
        this.uniqueIdCache = uniqueIdCache;
        this.settings = settings;

        ServicePointManager.Expect100Continue = false;

#if NET6_0_OR_GREATER && !WIN32
        var sslOptions = new SslClientAuthenticationOptions()
        {
            CipherSuitesPolicy = new CipherSuitesPolicy(new[] { TlsCipherSuite.TLS_DHE_RSA_WITH_AES_256_GCM_SHA384 })
        };

        var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            SslOptions = sslOptions,
        };
#else
        var handler = new HttpClientHandler
        {
            UseCookies = false,
        };
#endif

        this.client = new HttpClient(handler);
    }

    // The user agent for frontier pages. {0} has to be replaced by a unique computer id and its checksum
    private const string USER_AGENT_TEMPLATE = "SQEXAuthor/2.0.0(Windows 6.2; ja-jp; {0})";
    private readonly string _userAgent = GenerateUserAgent();

    private static readonly string[] FilesToHash =
    {
        "ffxivboot.exe",
        "ffxivboot64.exe",
        "ffxivlauncher.exe",
        "ffxivlauncher64.exe",
        "ffxivupdater.exe",
        "ffxivupdater64.exe"
    };

    public virtual async Task<LoginResult> Login(string userName, string password, string otp, bool useCache, DirectoryInfo gamePath, bool forceBaseVersion, bool isFreeTrial)
    {
        PatchListEntry[] pendingPatches = Array.Empty<PatchListEntry>();

        LoginState loginState;

        Log.Information("SqexLauncher::Login(cache:{UseCache})", useCache);

        if (!useCache || !this.uniqueIdCache.TryGet(userName, out var cached))
        {
            this.oauthLoginResult = await OauthLogin(userName, password, otp, isFreeTrial, 3);

            Log.Information(
                $"OAuth login successful - playable:{oauthLoginResult.Playable} terms:{oauthLoginResult.TermsAccepted} region:{oauthLoginResult.Region} expack:{oauthLoginResult.MaxExpansion}");

            if (!this.oauthLoginResult.Playable)
            {
                return new LoginResult
                {
                    State = LoginState.NoService
                };
            }

            if (!this.oauthLoginResult.TermsAccepted)
            {
                return new LoginResult
                {
                    State = LoginState.NoTerms
                };
            }

            try
            {
                pendingPatches = await CheckGameVersion(gamePath, forceBaseVersion);
                loginState = pendingPatches.Length > 0 ? LoginState.NeedsPatchGame : LoginState.Ok;
            }
            catch (VersionCheckLoginException ex)
            {
                loginState = ex.State;
            }

            if (useCache)
                this.uniqueIdCache.Add(userName, this.uniqueId, oauthLoginResult.Region, oauthLoginResult.MaxExpansion);
        }
        else
        {
            Log.Information("Cached UID found, using instead");
            this.uniqueId = cached.UniqueId;
            loginState = LoginState.Ok;

            this.oauthLoginResult = new OauthLoginResult
            {
                Playable = true,
                Region = cached.Region,
                TermsAccepted = true,
                MaxExpansion = cached.MaxExpansion
            };
        }

        return new LoginResult
        {
            PendingPatches = pendingPatches,
            OauthLogin = this.oauthLoginResult,
            State = loginState,
            UniqueId = this.uniqueId
        };
    }
    
    protected virtual void ModifyGameLaunchOptions(Dictionary<string, string> environment, ArgumentBuilder argumentBuilder)
    {
        // no-op for SqexLauncher, overridden by SteamSqexLauncher
    }

    public Process? LaunchGame(IGameRunner runner, string sessionId, int region, int expansionLevel,
                              string additionalArguments, DirectoryInfo gamePath, bool isDx11,
                              ClientLanguage language, bool encryptArguments, DpiAwareness dpiAwareness)
    {
        Log.Information(
            $"SqexLauncher::LaunchGame(args:{additionalArguments})");

        var exePath = Path.Combine(gamePath.FullName, "game", "ffxiv_dx11.exe");
        if (!isDx11)
            exePath = Path.Combine(gamePath.FullName, "game", "ffxiv.exe");

        var environment = new Dictionary<string, string>();

        var argumentBuilder = new ArgumentBuilder()
                              .Append("DEV.DataPathType", "1")
                              .Append("DEV.MaxEntitledExpansionID", expansionLevel.ToString())
                              .Append("DEV.TestSID", sessionId)
                              .Append("DEV.UseSqPack", "1")
                              .Append("SYS.Region", region.ToString())
                              .Append("language", ((int)language).ToString())
                              .Append("resetConfig", "0")
                              .Append("ver", Repository.Ffxiv.GetVer(gamePath));

        ModifyGameLaunchOptions(environment, argumentBuilder);

        // This is a bit of a hack; ideally additionalArguments would be a dictionary or some KeyValue structure
        if (!string.IsNullOrEmpty(additionalArguments))
        {
            var regex = new Regex(@"\s*(?<key>[^\s=]+)\s*=\s*(?<value>([^=]*$|[^=]*\s(?=[^\s=]+)))\s*", RegexOptions.Compiled);
            foreach (Match match in regex.Matches(additionalArguments))
                argumentBuilder.Append(match.Groups["key"].Value, match.Groups["value"].Value.Trim());
        }

        if (!File.Exists(exePath))
            throw new BinaryNotPresentException(exePath);

        var workingDir = Path.Combine(gamePath.FullName, "game");

        var arguments = encryptArguments
            ? argumentBuilder.BuildEncrypted()
            : argumentBuilder.Build();

        return runner.Start(exePath, workingDir, arguments, environment, dpiAwareness);
    }

    private static string GetVersionReport(DirectoryInfo gamePath, int exLevel, bool forceBaseVersion)
    {
        var verReport = $"{GetBootVersionHash(gamePath)}";

        if (exLevel >= 1)
            verReport += $"\nex1\t{(forceBaseVersion ? Constants.BASE_GAME_VERSION : Repository.Ex1.GetVer(gamePath))}";

        if (exLevel >= 2)
            verReport += $"\nex2\t{(forceBaseVersion ? Constants.BASE_GAME_VERSION : Repository.Ex2.GetVer(gamePath))}";

        if (exLevel >= 3)
            verReport += $"\nex3\t{(forceBaseVersion ? Constants.BASE_GAME_VERSION : Repository.Ex3.GetVer(gamePath))}";

        if (exLevel >= 4)
            verReport += $"\nex4\t{(forceBaseVersion ? Constants.BASE_GAME_VERSION : Repository.Ex4.GetVer(gamePath))}";

        return verReport;
    }

    /// <summary>
    /// Calculate the hash that is sent to patch-gamever for version verification/tamper protection.
    /// This same hash is also sent in lobby, but for ffxiv.exe and ffxiv_dx11.exe.
    /// </summary>
    /// <returns>String of hashed EXE files.</returns>
    private static string GetBootVersionHash(DirectoryInfo gamePath)
    {
        var result = Repository.Boot.GetVer(gamePath) + "=";

        for (var i = 0; i < FilesToHash.Length; i++)
        {
            result +=
                $"{FilesToHash[i]}/{GetFileHash(Path.Combine(gamePath.FullName, "boot", FilesToHash[i]))}";

            if (i != FilesToHash.Length - 1)
                result += ",";
        }

        return result;
    }

    public async Task<PatchListEntry[]> CheckBootVersion(DirectoryInfo gamePath, bool forceBaseVersion = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"http://patch-bootver.ffxiv.com/http/win32/ffxivneo_release_boot/{(forceBaseVersion ? Constants.BASE_GAME_VERSION : Repository.Boot.GetVer(gamePath))}/?time=" +
            GetLauncherFormattedTimeLongRounded());

        request.Headers.AddWithoutValidation("User-Agent", Constants.PatcherUserAgent);
        request.Headers.AddWithoutValidation("Host", "patch-bootver.ffxiv.com");

        var resp = await this.client.SendAsync(request);
        var text = await resp.Content.ReadAsStringAsync();

        if (text == string.Empty)
            return Array.Empty<PatchListEntry>();

        Log.Verbose("Boot patching is needed... List:\n{PatchList}", resp);

        return PatchListParser.Parse(text);
    }

    public async Task<PatchListEntry[]> CheckGameVersion(DirectoryInfo gamePath, bool forceBaseVersion = false)
    {
        if (this.oauthLoginResult == null)
        {
            throw new VersionCheckLoginException(LoginState.NoLogin);
        }
        
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://patch-gamever.ffxiv.com/http/win32/ffxivneo_release_game/{(forceBaseVersion ? Constants.BASE_GAME_VERSION : Repository.Ffxiv.GetVer(gamePath))}/{this.oauthLoginResult.SessionId}");

        request.Headers.AddWithoutValidation("X-Hash-Check", "enabled");
        request.Headers.AddWithoutValidation("User-Agent", Constants.PatcherUserAgent);

        request.Content = new StringContent(GetVersionReport(gamePath, this.oauthLoginResult.MaxExpansion, forceBaseVersion));

        var resp = await this.client.SendAsync(request);
        var text = await resp.Content.ReadAsStringAsync();

        // Conflict indicates that boot needs to update, we do not get a patch list or a unique ID to download patches with in this case
        if (resp.StatusCode == HttpStatusCode.Conflict)
            throw new VersionCheckLoginException(LoginState.NeedsPatchBoot);

        if (!resp.Headers.TryGetValues("X-Patch-Unique-Id", out var uidVals))
            throw new InvalidResponseException("Could not get X-Patch-Unique-Id.", text);

        this.uniqueId = uidVals.First();

        if (string.IsNullOrEmpty(text))
            return Array.Empty<PatchListEntry>();

        Log.Verbose("Game Patching is needed... List:\n{PatchList}", text);

        return PatchListParser.Parse(text);
    }

    public async Task<string> GenPatchToken(string patchUrl, string uniqueId)
    {
        // Yes, Square does use HTTP for this and sends tokens in headers. IT'S NOT MY FAULT.
        var request = new HttpRequestMessage(HttpMethod.Post, "http://patch-gamever.ffxiv.com/gen_token");

        request.Headers.AddWithoutValidation("Connection", "Keep-Alive");
        request.Headers.AddWithoutValidation("X-Patch-Unique-Id", uniqueId);
        request.Headers.AddWithoutValidation("User-Agent", Constants.PatcherUserAgent);

        request.Content = new StringContent(patchUrl);

        var resp = await this.client.SendAsync(request);
        resp.EnsureSuccessStatusCode();

        return await resp.Content.ReadAsStringAsync();
    }

    protected virtual async Task<(string Stored, string Text)> GetOauthTop(string url)
    {
        // This is needed to be able to access the login site correctly
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.AddWithoutValidation("Accept", "image/gif, image/jpeg, image/pjpeg, application/x-ms-application, application/xaml+xml, application/x-ms-xbap, */*");
        request.Headers.AddWithoutValidation("Referer", GenerateFrontierReferer(this.settings.ClientLanguage.GetValueOrDefault(ClientLanguage.English)));
        request.Headers.AddWithoutValidation("Accept-Encoding", "gzip, deflate");
        request.Headers.AddWithoutValidation("Accept-Language", this.settings.AcceptLanguage);
        request.Headers.AddWithoutValidation("User-Agent", _userAgent);
        request.Headers.AddWithoutValidation("Connection", "Keep-Alive");
        request.Headers.AddWithoutValidation("Cookie", "_rsid=\"\"");

        var reply = await this.client.SendAsync(request);

        var text = await reply.Content.ReadAsStringAsync();

        if (text.Contains("window.external.user(\"restartup\");"))
        {
            throw new SteamLinkNeededException(text);
        }

        var storedRegex = new Regex(@"\t<\s*input .* name=""_STORED_"" value=""(?<stored>.*)"">");
        var matches = storedRegex.Matches(text);

        if (matches.Count == 0)
        {
            Log.Error(text);
            throw new InvalidResponseException("Could not get STORED.", text);
        }

        return (matches[0].Groups["stored"].Value, text);
    }

    protected virtual string GetOauthTopUrl(int region, bool isFreeTrial)
    {
        return $"https://ffxiv-login.square-enix.com/oauth/ffxivarr/login/top?lng=en&rgn={region}&isft={(isFreeTrial ? "1" : "0")}&cssmode=1&isnew=1&launchver=3";
    }

    protected virtual async Task<OauthLoginResult> OauthLogin(string userName, string password, string otp, bool isFreeTrial, int region)
    {
        var topUrl = GetOauthTopUrl(region, isFreeTrial);
        var topResult = await GetOauthTop(topUrl);

        try
        {
            return await DoOauthLogin(topResult.Stored, topUrl, userName, password, otp);
        }
        catch (SteamLinkNeededException ex)
        {
            throw new InvalidResponseException("restartup, but not isSteam?", ex.Document);
        }
    }

    protected async Task<OauthLoginResult> DoOauthLogin(string stored, string topUrl, string userName, string password, string otp)
    {
        var request = new HttpRequestMessage(HttpMethod.Post,
            "https://ffxiv-login.square-enix.com/oauth/ffxivarr/login/login.send");

        request.Headers.AddWithoutValidation("Accept", "image/gif, image/jpeg, image/pjpeg, application/x-ms-application, application/xaml+xml, application/x-ms-xbap, */*");
        request.Headers.AddWithoutValidation("Referer", topUrl);
        request.Headers.AddWithoutValidation("Accept-Language", this.settings.AcceptLanguage);
        request.Headers.AddWithoutValidation("User-Agent", _userAgent);
        //request.Headers.AddWithoutValidation("Content-Type", "application/x-www-form-urlencoded");
        request.Headers.AddWithoutValidation("Accept-Encoding", "gzip, deflate");
        request.Headers.AddWithoutValidation("Host", "ffxiv-login.square-enix.com");
        request.Headers.AddWithoutValidation("Connection", "Keep-Alive");
        request.Headers.AddWithoutValidation("Cache-Control", "no-cache");
        request.Headers.AddWithoutValidation("Cookie", "_rsid=\"\"");

        request.Content = new FormUrlEncodedContent(
            new Dictionary<string, string>()
            {
                { "_STORED_", stored },
                { "sqexid", userName },
                { "password", password },
                { "otppw", otp },
                // { "saveid", "1" } // NOTE(goat): This adds a Set-Cookie with a filled-out _rsid value in the login response.
            });

        var response = await this.client.SendAsync(request);

        var reply = await response.Content.ReadAsStringAsync();

        var regex = new Regex(@"window.external.user\(""login=auth,ok,(?<launchParams>.*)\);");
        var matches = regex.Matches(reply);

        if (matches.Count == 0)
            throw new OauthLoginException(reply);

        var launchParams = matches[0].Groups["launchParams"].Value.Split(',');

        return new OauthLoginResult
        {
            SessionId = launchParams[1],
            Region = int.Parse(launchParams[5]),
            TermsAccepted = launchParams[3] != "0",
            Playable = launchParams[9] != "0",
            MaxExpansion = int.Parse(launchParams[13])
        };
    }

    private static string GetFileHash(string file)
    {
        var bytes = File.ReadAllBytes(file);

        var hash = SHA1.Create().ComputeHash(bytes);
        var hashstring = string.Join("", hash.Select(b => b.ToString("x2")).ToArray());

        var length = new FileInfo(file).Length;

        return length + "/" + hashstring;
    }

    public async Task<GateStatus> GetGateStatus(ClientLanguage language)
    {
        try
        {
            var reply = Encoding.UTF8.GetString(
                await DownloadAsLauncher(
                    $"https://frontier.ffxiv.com/worldStatus/gate_status.json?lang={language.GetLangCode()}&_={ApiHelpers.GetUnixMillis()}", language).ConfigureAwait(true));

            return JsonConvert.DeserializeObject<GateStatus>(reply);
        }
        catch (Exception exc)
        {
            throw new Exception("Could not get gate status", exc);
        }
    }

    public async Task<bool> GetLoginStatus()
    {
        try
        {
            var reply = Encoding.UTF8.GetString(
                await DownloadAsLauncher(
                    $"https://frontier.ffxiv.com/worldStatus/login_status.json?_={ApiHelpers.GetUnixMillis()}", ClientLanguage.English).ConfigureAwait(true));

            return Convert.ToBoolean(int.Parse(reply[10].ToString()));
        }
        catch (Exception exc)
        {
            throw new Exception("Could not get gate status", exc);
        }
    }

    private static string MakeComputerId()
    {
        var hashString = Environment.MachineName + Environment.UserName + Environment.OSVersion +
                         Environment.ProcessorCount;

        using var sha1 = HashAlgorithm.Create("SHA1");

        var bytes = new byte[5];

        Array.Copy(sha1.ComputeHash(Encoding.Unicode.GetBytes(hashString)), 0, bytes, 1, 4);

        var checkSum = (byte)-(bytes[1] + bytes[2] + bytes[3] + bytes[4]);
        bytes[0] = checkSum;

        return BitConverter.ToString(bytes).Replace("-", "").ToLower();
    }

    public async Task<byte[]> DownloadAsLauncher(string url, ClientLanguage language, string contentType = "")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);

        request.Headers.AddWithoutValidation("User-Agent", _userAgent);

        if (!string.IsNullOrEmpty(contentType))
        {
            request.Headers.AddWithoutValidation("Accept", contentType);
        }

        request.Headers.AddWithoutValidation("Accept-Encoding", "gzip, deflate");
        request.Headers.AddWithoutValidation("Accept-Language", this.settings.AcceptLanguage);

        request.Headers.AddWithoutValidation("Origin", "https://launcher.finalfantasyxiv.com");

        request.Headers.AddWithoutValidation("Referer", GenerateFrontierReferer(language));

        var resp = await this.client.SendAsync(request);
        return await resp.Content.ReadAsByteArrayAsync();
    }

    private static string GenerateFrontierReferer(ClientLanguage language)
    {
        var langCode = language.GetLangCode().Replace("-", "_");
        var formattedTime = GetLauncherFormattedTimeLong();

        return $"https://launcher.finalfantasyxiv.com/v610/index.html?rc_lang={langCode}&time={formattedTime}";
    }

    // Used to be used for frontier top, they now use the un-rounded long timestamp
    private static string GetLauncherFormattedTime() => DateTime.UtcNow.ToString("yyyy-MM-dd-HH");

    private static string GetLauncherFormattedTimeLong() => DateTime.UtcNow.ToString("yyyy-MM-dd-HH-mm");

    private static string GetLauncherFormattedTimeLongRounded()
    {
        var formatted = DateTime.UtcNow.ToString("yyyy-MM-dd-HH-mm").ToCharArray();
        formatted[15] = '0';

        return new string(formatted);
    }

    private static string GenerateUserAgent()
    {
        return string.Format(USER_AGENT_TEMPLATE, MakeComputerId());
    }
}

#nullable restore
