using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

class Program
{
    private const int BraveryChampionId = -3;
    private const int PollDelayMilliseconds = 500;
    private const int InProgressDelayMilliseconds = 5_000;
    private const int ConnectionRetryDelayMilliseconds = 5_555;
    private const int BanLockThresholdMilliseconds = 2_222;

    private static int port;
    private static int pickChampId = BraveryChampionId;
    private static int banIndex = -1;
    private static bool instaBan;
    private static readonly bool pickChampInput = false;

    private static readonly List<int> champBanIds = [];
    private static readonly Dictionary<string, int> champions = [];

    private static readonly HttpClient client = new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true
    })
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    private static readonly HashSet<int> processedActions = [];

    private static async Task Main()
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
            Console.WriteLine("\nStopping...");
        };

        try
        {
            await RunAsync(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Console.WriteLine("Stopped.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Fatal error: {ex.Message}");
        }
    }

    private static async Task RunAsync(CancellationToken cancellationToken)
    {
        await PopulateChampDictionary(cancellationToken);
        PickInput();
        BanInput();

        var previousPhase = "NULL";
        while (!cancellationToken.IsCancellationRequested)
        {
            var phase = await GetPhase(cancellationToken);
            if (phase != previousPhase)
            {
                Console.WriteLine($"Entered \"{phase ?? "NULL"}\" phase from \"{previousPhase}\"");
                if (phase != "ChampSelect") ResetChampSelectState();
            }

            await HandleGamePhase(phase, cancellationToken);

            previousPhase = phase ?? "NULL";

            await Task.Delay(
                GetPollDelay(phase),
                cancellationToken);
        }
    }

    private static async Task HandleGamePhase(string? phase, CancellationToken cancellationToken)
    {
        switch (phase)
        {
            case "ReadyCheck":
                await AcceptReadyCheck(cancellationToken);
                return;
            case "ChampSelect":
                await HandleChampSelect(cancellationToken);
                return;
            case "InProgress":
                await Task.Delay(
                    InProgressDelayMilliseconds,
                    cancellationToken);
                return;
            case null:
                Console.WriteLine("\nDisconnected from league client");
                await ConnectToLeagueClient(cancellationToken);
                return;
        }
    }

    private static int GetPollDelay(string? phase)
    {
        return phase == "ChampSelect" ? PollDelayMilliseconds : 1_000;
    }

    private static async Task AcceptReadyCheck(CancellationToken cancellationToken)
    {
        using var response = await PostAsync("/lol-matchmaking/v1/ready-check/accept", null, cancellationToken);
        if (!response.IsSuccessStatusCode) Console.WriteLine($"Failed to accept ready check: {(int)response.StatusCode}");
    }

    private static void PickInput()
    {
        if (!pickChampInput) return;

        while (true)
        {
            Console.WriteLine("Type in name of champ you want to PLAY... (bravery (or leave empty) for BRAVERY)");

            var input = NormalizeChampionName(Console.ReadLine());
            if (string.IsNullOrEmpty(input) || input == "bravery")
            {
                pickChampId = BraveryChampionId;
                return;
            }
            if (champions.TryGetValue(input, out var championId))
            {
                pickChampId = championId;
                return;
            }

            Console.WriteLine($"\"{input}\" is not valid... U ARE BAD AT SPELLING... try again");
        }
    }

    private static void BanInput()
    {
        while (true)
        {
            Console.WriteLine("Type in name of champ(s) you want to BAN in order of priority... (example: \"ryze, aurelion sol, vladimir, belveth, jarvaniv\")");

            var input = Console.ReadLine() ?? "";
            var badChamps = new List<string>();
            foreach (var rawName in input.Split(','))
            {
                var name = NormalizeChampionName(rawName);

                if (string.IsNullOrEmpty(name))
                    continue;

                if (!champions.TryGetValue(name, out var championId))
                {
                    badChamps.Add($"\"{name}\"");
                    continue;
                }

                if (!champBanIds.Contains(championId))
                    champBanIds.Add(championId);
            }
            if (badChamps.Count == 0) break;

            Console.WriteLine($"{string.Join(", ", badChamps)} are not valid.");
        }
        instaBan = champBanIds.Count == 1;
    }

    private static string NormalizeChampionName(string? input)
    {
        return (input ?? "")
            .Trim()
            .ToLowerInvariant()
            .Replace(" ", "")
            .Replace("'", "");
    }

    private static async Task HandleChampSelect(CancellationToken cancellationToken)
    {
        using var sessionResponse = await GetAsync(
            "/lol-champ-select/v1/session",
            cancellationToken);

        if (!sessionResponse.IsSuccessStatusCode)
        {
            Console.WriteLine($"Couldn't get champ select session data: {(int)sessionResponse.StatusCode}");
            return;
        }

        var sessionJson = await sessionResponse.Content.ReadAsStringAsync(cancellationToken);

        using var document = JsonDocument.Parse(sessionJson);
        var root = document.RootElement;
        var phase = GetChampSelectPhase(root);
        if (phase == "PLANNING") return;
        LogUnknownChampSelectPhase(phase);

        var playerCellId = root.GetProperty("localPlayerCellId").GetInt32();
        var timer = root.GetProperty("timer");
        var actions = root
            .GetProperty("actions")
            .EnumerateArray()
            .SelectMany(group => group.EnumerateArray())
            .ToArray();

        foreach (var action in actions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsPendingPlayerAction(action, playerCellId)) continue;
            await HandleAction(action, timer, cancellationToken);
        }
    }

    private static string GetChampSelectPhase(JsonElement root)
    {
        return root
            .GetProperty("timer")
            .GetProperty("phase")
            .GetString() ?? "";
    }

    private static void LogUnknownChampSelectPhase(string phase)
    {
        if (phase is "FINALIZATION" or "BAN_PICK" or "GAME_STARTING") return;

        Console.WriteLine($"new champ select phase just dropped: \"{phase}\"");
    }

    private static bool IsPendingPlayerAction(JsonElement action, int playerCellId)
    {
        if (!action.GetProperty("isInProgress").GetBoolean()) return false;
        if (action.GetProperty("actorCellId").GetInt32() != playerCellId) return false;
        return !action.GetProperty("completed").GetBoolean();
    }

    private static async Task HandleAction(JsonElement action, JsonElement timer, CancellationToken cancellationToken)
    {
        var actionId = action.GetProperty("id").GetInt32();
        var type = action.GetProperty("type").GetString();
        if (type is null)
        {
            Console.WriteLine($"Action {actionId} has no type.");
            return;
        }
        if (processedActions.Contains(actionId)) return;

        switch (type)
        {
            case "ban":
                await HandleBanAction(action, timer, cancellationToken);
                return;
            case "pick":
                await HandlePickAction(action, cancellationToken);
                return;
        }
    }

    private static async Task HandleBanAction(JsonElement action, JsonElement timer, CancellationToken cancellationToken)
    {
        var actionId = action.GetProperty("id").GetInt32();
        var championId = action.GetProperty("championId").GetInt32();

        if (championId <= 0)
        {
            var nextBanId = GetNextBanId();
            if (!nextBanId.HasValue)
            {
                Console.WriteLine("No configured ban remains for this ban action.");

                processedActions.Add(actionId);
                return;
            }
            banIndex++;

            var selected = await SelectChamp(actionId, nextBanId.Value, cancellationToken);
            if (!selected) return;
            championId = nextBanId.Value;
        }

        var timeLeft = timer.GetProperty("adjustedTimeLeftInPhase").GetInt32();
        var shouldLock = instaBan || timeLeft <= BanLockThresholdMilliseconds;
        if (!shouldLock) return;

        Console.WriteLine($"Banning champion {championId} with {timeLeft} milliseconds left");

        var locked = await LockIn(actionId, championId, cancellationToken);
        if (locked) processedActions.Add(actionId);
    }

    private static int? GetNextBanId()
    {
        var nextIndex = banIndex + 1;
        if (nextIndex < 0 || nextIndex >= champBanIds.Count) return null;
        return champBanIds[nextIndex];
    }

    private static async Task HandlePickAction(JsonElement action, CancellationToken cancellationToken)
    {
        var actionId = action.GetProperty("id").GetInt32();
        var currentChampionId = action.GetProperty("championId").GetInt32();

        if (!pickChampInput)
        {
            var locked = await LockIn(
                actionId,
                pickChampId,
                cancellationToken);

            if (locked) processedActions.Add(actionId);
            return;
        }

        var valid = await IsValidPick(
            pickChampId,
            cancellationToken);

        if (!valid)
        {
            Console.WriteLine($"Champion ID {pickChampId} is not currently pickable.");
            return;
        }

        if (currentChampionId != pickChampId)
        {
            var selected = await SelectChamp(
                actionId,
                pickChampId,
                cancellationToken);

            if (!selected) return;
        }

        var result = await LockIn(
            actionId,
            pickChampId,
            cancellationToken);

        if (result) processedActions.Add(actionId);
    }

    private static async Task<bool> IsValidPick(int championId, CancellationToken cancellationToken)
    {
        if (championId == BraveryChampionId) return true;

        using var response = await GetAsync("/lol-champ-select/v1/pickable-champion-ids", cancellationToken);

        if (!response.IsSuccessStatusCode) return false;
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        using var document = JsonDocument.Parse(json);
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (!element.TryGetInt32(out var id)) continue;
            if (id == championId) return true;
            if (id < 1 && id != BraveryChampionId) Console.WriteLine($"There is a pickable special non-bravery champion ID: {id}.");
        }

        return false;
    }

    private static async Task<bool> SelectChamp(int actionId, int championId, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { championId });
        using var content = new StringContent(
            payload,
            Encoding.UTF8,
            "application/json");
        using var response = await PatchAsync(
            $"/lol-champ-select/v1/session/actions/{actionId}",
            content,
            cancellationToken);

        if (response.IsSuccessStatusCode) return true;
        Console.WriteLine($"Failed to select champion {championId} for action {actionId}: {(int)response.StatusCode}");

        return false;
    }

    private static async Task<bool> LockIn(int actionId, int championId, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(
            new
            {
                championId,
                completed = true
            });

        using var content = new StringContent(
            payload,
            Encoding.UTF8,
            "application/json");

        using var response = await PatchAsync(
            $"/lol-champ-select/v1/session/actions/{actionId}",
            content,
            cancellationToken);

        if (response.IsSuccessStatusCode) return true;

        Console.WriteLine($"Failed to lock in champion {championId} for action {actionId}: {(int)response.StatusCode}");

        return false;
    }

    private static async Task<HttpResponseMessage> PostAsync(string path, HttpContent? content, CancellationToken cancellationToken)
    {
        return await client.PostAsync(
            BuildClientUri(path),
            content,
            cancellationToken);
    }

    private static async Task<HttpResponseMessage> PatchAsync(string path, HttpContent content, CancellationToken cancellationToken)
    {
        return await client.PatchAsync(
            BuildClientUri(path),
            content,
            cancellationToken);
    }

    private static async Task<HttpResponseMessage> GetAsync(string path, CancellationToken cancellationToken)
    {
        return await client.GetAsync(
            BuildClientUri(path),
            cancellationToken);
    }

    private static Uri BuildClientUri(string path)
    {
        return new Uri($"https://127.0.0.1:{port}{path}");
    }

    private static async Task<string?> FindLockPath()
    {
        var defaultPaths = new[]
        {
        @"C:\Riot Games\League of Legends\lockfile",
        @"C:\Games\Riot Games\League of Legends\lockfile",
        @"C:\Program Files\Riot Games\League of Legends\lockfile",
        @"C:\Program Files (x86)\Riot Games\League of Legends\lockfile"
    };

        foreach (var path in defaultPaths)
            if (Path.Exists(path))
            {
                Console.WriteLine($"Found lockfile using default path: {path}");

                return path;
            }

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (!IsLeagueClientProcess(process)) continue;

                var executablePath = process.MainModule?.FileName;
                if (string.IsNullOrEmpty(executablePath)) continue;

                var directory = Path.GetDirectoryName(executablePath);
                if (string.IsNullOrEmpty(directory)) continue;

                var lockPath = Path.Combine(directory, "lockfile");
                if (!Path.Exists(lockPath)) continue;

                Console.WriteLine($"Found lockfile using running process: {lockPath}");
                return lockPath;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Console.WriteLine($"Unable to inspect process {process.Id} ({process.ProcessName}): {ex.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }

        return null;
    }

    private static bool IsLeagueClientProcess(Process process)
    {
        return process.ProcessName.Contains("LeagueClient", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string?> ReadLockfile(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync(cancellationToken);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task ConnectToLeagueClient(CancellationToken cancellationToken)
    {
        Console.WriteLine("Connecting to league client...");

        while (!cancellationToken.IsCancellationRequested)
        {
            var lockPath = await FindLockPath();
            if (lockPath is not null)
            {
                var lockContent =
                    await ReadLockfile(
                        lockPath,
                        cancellationToken);

                if (TryConfigureClient(lockContent))
                {
                    Console.WriteLine("Successfully connected HTTP client to League Client");

                    await WaitForClientReady(cancellationToken);
                    return;
                }
            }

            Console.WriteLine("Failed getting lockfile. Retrying in {ConnectionRetryDelayMilliseconds} ms... (open your client)");

            await Task.Delay(ConnectionRetryDelayMilliseconds, cancellationToken);
        }
    }

    private static bool TryConfigureClient(string? lockContent)
    {
        if (string.IsNullOrWhiteSpace(lockContent)) return false;
        var parts = lockContent.Trim().Split(':');
        if (parts.Length < 5)
        {
            Console.WriteLine("Invalid lockfile: expected at least 5 fields.");
            return false;
        }
        if (!int.TryParse(parts[2], out var clientPort))
        {
            Console.WriteLine($"Invalid lockfile port: {parts[2]}");
            return false;
        }
        if (string.IsNullOrWhiteSpace(parts[3]))
        {
            Console.WriteLine("Invalid lockfile: missing password.");
            return false;
        }

        port = clientPort;

        var authBytes = Encoding.UTF8.GetBytes($"riot:{parts[3]}");
        var auth = Convert.ToBase64String(authBytes);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", auth);

        return true;
    }

    private static async Task WaitForClientReady(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var phase = await GetPhase(cancellationToken);
            if (phase is not null) return;

            await Task.Delay(PollDelayMilliseconds, cancellationToken);
        }
    }

    private static async Task<string?> GetPhase(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await GetAsync("/lol-gameflow/v1/gameflow-phase", cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            return content.Trim('"');
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static async Task PopulateChampDictionary(CancellationToken cancellationToken)
    {
        Console.WriteLine("Loading champion dictionary...");

        const string versionsUrl = "https://ddragon.leagueoflegends.com/api/versions.json";

        using var versionsResponse = await client.GetAsync(versionsUrl, cancellationToken);

        if (!versionsResponse.IsSuccessStatusCode)
        {
            throw new Exception("Failed to retrieve Data Dragon versions.");
        }

        var versionsJson = await versionsResponse.Content.ReadAsStringAsync(cancellationToken);

        using var versionsDocument = JsonDocument.Parse(versionsJson);

        var latestVersion = versionsDocument.RootElement
            .EnumerateArray()
            .FirstOrDefault()
            .GetString();

        if (string.IsNullOrWhiteSpace(latestVersion)) throw new Exception("Data Dragon returned no versions.");

        var championUrl = $"https://ddragon.leagueoflegends.com/cdn/{latestVersion}/data/en_US/champion.json";

        using var response = await client.GetAsync(championUrl, cancellationToken);

        if (!response.IsSuccessStatusCode) throw new Exception($"Failed to retrieve champion data for Data Dragon {latestVersion}.");

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(json);
        var data = document.RootElement.GetProperty("data");

        champions.Clear();
        foreach (var champion in data.EnumerateObject())
        {
            var idString = champion.Value
                .GetProperty("key")
                .GetString();
            var name = champion.Value
                .GetProperty("name")
                .GetString();
            if (string.IsNullOrWhiteSpace(idString) || string.IsNullOrWhiteSpace(name) || !int.TryParse(idString, out var id)) continue;
            champions[NormalizeChampionName(name)] = id;
        }

        Console.WriteLine($"Loaded {champions.Count} champions from Data Dragon {latestVersion}");
    }

    private static void ResetChampSelectState()
    {
        banIndex = -1;
        processedActions.Clear();
    }
}
