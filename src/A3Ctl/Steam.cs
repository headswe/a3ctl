using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace A3Ctl;

public sealed record SteamCmdResult(int ExitCode, List<string> Output, string LogFile);

public sealed class SteamCmd(App app)
{
    public const long ArmaClientAppId = 107410;

    private string? Password => Environment.GetEnvironmentVariable("A3CTL_STEAM_PASSWORD") is { Length: > 0 } p ? p : null;

    // Runs `steamcmd +force_install_dir <dir> +login ... <commands> +quit`, streaming output
    // to the terminal and to a log file under logs/steamcmd. onLine sees every non-empty line.
    public async Task<SteamCmdResult> RunAsync(
        string installDir, IEnumerable<string> commands, string logName, CancellationToken ct, Action<string>? onLine = null)
    {
        var psi = new ProcessStartInfo(app.Config.SteamCmd)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };

        psi.ArgumentList.Add("+force_install_dir");
        psi.ArgumentList.Add(installDir);
        psi.ArgumentList.Add("+login");
        psi.ArgumentList.Add(app.Config.SteamUser ?? "anonymous");
        if (app.Config.SteamUser is not null && Password is { } password)
        {
            psi.ArgumentList.Add(password);
        }

        foreach (var command in commands)
        {
            psi.ArgumentList.Add(command);
        }

        psi.ArgumentList.Add("+quit");

        var logDir = Path.Combine(app.LogsDir, "steamcmd");
        Directory.CreateDirectory(logDir);
        var logFile = Path.Combine(logDir, $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{logName}.log");
        await using var log = new StreamWriter(logFile) { AutoFlush = true };
        await log.WriteLineAsync("$ " + Redact(string.Join(' ', psi.ArgumentList.Prepend(psi.FileName))));

        Process process;
        try
        {
            process = Process.Start(psi)!;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new UserException($"could not run SteamCMD '{app.Config.SteamCmd}': {ex.Message} (set steamCmd in the config)");
        }

        using (process)
        {
            // SteamCMD prompts for Steam Guard / password on stdin when the cached login
            // is missing; closing stdin makes it fail instead of hanging forever.
            process.StandardInput.Close();

            var output = new List<string>();
            var gate = new object();

            async Task Pump(StreamReader reader)
            {
                while (await reader.ReadLineAsync(ct) is { } line)
                {
                    line = Redact(line);
                    lock (gate)
                    {
                        output.Add(line);
                        log.WriteLine(line);
                        var trimmed = line.Trim();
                        if (trimmed.Length > 0)
                        {
                            // Under a live progress display only problems are worth a line;
                            // everything is in the log file anyway.
                            if (Ui.Verbose || !Ui.Interactive)
                            {
                                Ui.Detail("steamcmd | " + trimmed);
                            }
                            else if (trimmed.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
                                     trimmed.Contains("FAILED", StringComparison.OrdinalIgnoreCase) ||
                                     trimmed.Contains("Login Failure", StringComparison.OrdinalIgnoreCase))
                            {
                                Ui.Warn("steamcmd: " + trimmed);
                            }

                            onLine?.Invoke(trimmed);
                        }
                    }
                }
            }

            try
            {
                await Task.WhenAll(Pump(process.StandardOutput), Pump(process.StandardError));
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }

            var joined = string.Join('\n', output);
            var loggedIn = joined.Contains("Waiting for user info...OK", StringComparison.OrdinalIgnoreCase) ||
                           joined.Contains("Logged in OK", StringComparison.OrdinalIgnoreCase);
            if (!loggedIn && app.Config.SteamUser is not null && (
                joined.Contains("Two-factor code", StringComparison.OrdinalIgnoreCase) ||
                joined.Contains("Steam Guard", StringComparison.OrdinalIgnoreCase) ||
                joined.Contains("password:", StringComparison.OrdinalIgnoreCase) ||
                joined.Contains("Invalid Password", StringComparison.OrdinalIgnoreCase)))
            {
                throw new UserException(
                    $"SteamCMD needs to log in as '{app.Config.SteamUser}'. Run `{app.Config.SteamCmd} +login {app.Config.SteamUser} +quit` " +
                    "once as this user to cache the session (Steam Guard), then retry.");
            }

            return new SteamCmdResult(process.ExitCode, output, logFile);
        }
    }

    private string Redact(string text) => Password is { } p ? text.Replace(p, "********") : text;
}

public sealed record WorkshopItem(long Id, bool Found, string Title, long TimeUpdated, long FileSize, long AppId);

// Steam Web API calls that do not need an API key.
public static partial class WorkshopApi
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    [GeneratedRegex(@"[?&]id=(\d+)")]
    private static partial Regex UrlIdPattern();

    public static long ParseId(string value)
    {
        value = value.Trim();
        if (long.TryParse(value, out var id) && id > 0)
        {
            return id;
        }

        var match = UrlIdPattern().Match(value);
        if (match.Success)
        {
            return long.Parse(match.Groups[1].Value);
        }

        throw new UserException($"'{value}' is not a Workshop ID or URL");
    }

    // "CBA_A3" -> "@cba_a3", "Enhanced Movement Rework" -> "@enhanced_movement_rework"
    public static string SuggestName(string title)
    {
        var slug = Regex.Replace(title.ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');
        if (slug.Length > 60)
        {
            slug = slug[..60].TrimEnd('_');
        }

        return "@" + (slug.Length == 0 ? "mod" : slug);
    }

    public static async Task<Dictionary<long, WorkshopItem>> GetDetailsAsync(IReadOnlyCollection<long> ids, CancellationToken ct)
    {
        var result = new Dictionary<long, WorkshopItem>();
        foreach (var chunk in ids.Distinct().Chunk(100))
        {
            var form = new List<KeyValuePair<string, string>> { new("itemcount", chunk.Length.ToString()) };
            form.AddRange(chunk.Select((id, i) => new KeyValuePair<string, string>($"publishedfileids[{i}]", id.ToString())));

            using var doc = await PostAsync("ISteamRemoteStorage/GetPublishedFileDetails/v1/", form, ct);
            if (!doc.RootElement.GetProperty("response").TryGetProperty("publishedfiledetails", out var details))
            {
                continue;
            }

            foreach (var item in details.EnumerateArray())
            {
                var id = Long(item, "publishedfileid");
                var found = Long(item, "result") == 1;
                result[id] = new WorkshopItem(
                    id,
                    found,
                    found && item.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "",
                    Long(item, "time_updated"),
                    Long(item, "file_size"),
                    Long(item, "consumer_app_id"));
            }
        }

        return result;
    }

    public static async Task<List<long>> GetCollectionAsync(long collectionId, CancellationToken ct)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("collectioncount", "1"),
            new("publishedfileids[0]", collectionId.ToString()),
        };

        using var doc = await PostAsync("ISteamRemoteStorage/GetCollectionDetails/v1/", form, ct);
        var collection = doc.RootElement.GetProperty("response").GetProperty("collectiondetails")[0];
        if (!collection.TryGetProperty("children", out var children))
        {
            throw new UserException($"Workshop item {collectionId} is not a public collection");
        }

        // filetype 0 = item; nested collections (2) are skipped.
        return children.EnumerateArray()
            .Where(c => Long(c, "filetype") == 0)
            .OrderBy(c => Long(c, "sortorder"))
            .Select(c => Long(c, "publishedfileid"))
            .ToList();
    }

    private static async Task<JsonDocument> PostAsync(string endpoint, List<KeyValuePair<string, string>> form, CancellationToken ct)
    {
        try
        {
            using var response = await Http.PostAsync(
                "https://api.steampowered.com/" + endpoint, new FormUrlEncodedContent(form), ct);
            response.EnsureSuccessStatusCode();
            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        }
        catch (HttpRequestException ex)
        {
            throw new UserException($"Steam Web API request failed: {ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new UserException("Steam Web API request timed out");
        }
    }

    // Steam returns some numbers as strings.
    private static long Long(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetInt64(),
            JsonValueKind.String when long.TryParse(value.GetString(), out var parsed) => parsed,
            _ => 0,
        };
    }
}
