using System.Text;
using System.Text.Json;
using System.Diagnostics;

class Program
{    
    private static int port;
    private static int pickChampId = -3;
    private static int banIndex = -1;
    private static bool instaBan = false;
    private static readonly bool pickChampInput = false;
    private static readonly List<int> champBanIds = [];
    private static readonly Dictionary<string, int> champions = [];
    private static readonly HttpClient client = new(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true })
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    private static async Task Main()
    {
        await ConnectToLeagueClient();
        await PopulateChampDictionary();
        PickInput();
        BanInput();
        var previousPhase = "NULL";
        while (true)
        {
            var phase = await GetPhase() ?? null;
            if (phase != previousPhase)
            {
                Console.WriteLine($"Entered \"{phase}\" phase from \"{previousPhase}\"");
            }
            switch (phase)
            {
                case "ReadyCheck":
                    await PostAsync("/lol-matchmaking/v1/ready-check/accept", null);
                    banIndex = -1;
                    break;
                case "ChampSelect":
                    await HandleChampSelect();
                    break;
                case "InProgress":
                    await Task.Delay(11111);
                    break;
                case null:
                    Console.WriteLine("\nDisconnected from league client");
                    await ConnectToLeagueClient();
                    break;
                default:
                    break;
            }
            previousPhase = phase;
            await Task.Delay(1111);
        }
    }

    private static void PickInput()
    {
        while (pickChampInput)
        {
            Console.WriteLine("Type in name of champ you want to PLAY... (bravery (or leave empty) for BRAVERY)");
            var input = Console.ReadLine()?.ToLower().Replace(" ", "");
            if (input == "" || input == "bravery")
            {
                pickChampId = -3;
                break;
            }
            else if (input != null && champions.TryGetValue(input, out pickChampId)) break;
            else Console.WriteLine($"\"{input}\" is not valid...U ARE BAD AT SPELLING...try again");
        }
    }

    private static void BanInput()
    {
        while (true)
        {
            Console.WriteLine("Type in name of champ(s) you want to BAN in order of priority... (example: \"ryze, aurelion sol, vladimir, belveth, jarvaniv\")");
            var input = Console.ReadLine()?.ToLower().Replace(" ", "") ?? "";
            List<string> badChamps = [];
            foreach (var s in input.Split(","))
            {
                if (!champions.ContainsKey(s))
                {
                    badChamps.Add($"\"{s}\"");
                    continue;
                }
                var id = champions[s];
                if (!champBanIds.Contains(id)) champBanIds.Add(id);
            }
            if (badChamps.Count == 0) break;
            else Console.WriteLine($"{string.Join(", ", badChamps)} are not valid.");
        }
        if (champBanIds.Count == 1) instaBan = true;
    }

    private static async Task<HttpResponseMessage> PostAsync(string s, HttpContent? c)
    {
        return await client.PostAsync($"https://127.0.0.1:{port}{s}", c);
    }

    private static async Task<HttpResponseMessage> PatchAsync(string s, HttpContent? c)
    {
        return await client.PatchAsync($"https://127.0.0.1:{port}{s}", c);
    }

    private static async Task<HttpResponseMessage> GetAsync(string s)
    {
        return await client.GetAsync($"https://127.0.0.1:{port}{s}");
    }

    private static async Task HandleChampSelect()
    {
        var sessionResp = await GetAsync("/lol-champ-select/v1/session");
        if (sessionResp == null || !sessionResp.IsSuccessStatusCode)
        {
            Console.WriteLine("Couldn't get champ select session data");
            return;
        }
        var sessionJson = await sessionResp.Content.ReadAsStringAsync();
        using var sessionJsonDoc = JsonDocument.Parse(sessionJson);
        var root = sessionJsonDoc.RootElement;
        var playerCellId = root.GetProperty("localPlayerCellId").GetInt32();
        var actions = root.GetProperty("actions").EnumerateArray().SelectMany(aa => aa.EnumerateArray()).ToArray();
        var timer = root.GetProperty("timer");
        var champSelectPhase = timer.GetProperty("phase").GetString() ?? "";
        if (champSelectPhase == "PLANNING") return;
        List<string> newPhase = ["FINALIZATION", "BAN_PICK", "GAME_STARTING"];
        if (!newPhase.Contains(champSelectPhase))
            Console.WriteLine($"new champ select phase just dropped: \"{champSelectPhase}\"");
        foreach (var action in actions)
        {
            if (!action.GetProperty("isInProgress").GetBoolean()) continue;
            if (action.GetProperty("actorCellId").GetInt32() != playerCellId) continue;
            if (action.GetProperty("completed").GetBoolean()) continue;
            var id = action.GetProperty("id").GetInt32();
            var type = action.GetProperty("type").GetString() ?? throw new Exception("action has null type");
            var champId = action.GetProperty("championId").GetInt32();
            if (type == "ban")
            {
                if (champId <= 0)
                {
                    banIndex++;
                    await SelectChamp(id, champBanIds[banIndex]);
                }
                if (instaBan || timer.GetProperty("adjustedTimeLeftInPhase").GetInt32() < 3333 || banIndex - 1 >= champBanIds.Count)
                    await LockIn(id, champBanIds[banIndex]);
            }
            else if (type == "pick")
            {
                if (!pickChampInput)
                {
                    await LockIn(id, pickChampId);
                }
                var validChampId = pickChampId == -3;
                if (champId != pickChampId)
                {
                    var pchamp = await GetAsync("/lol-champ-select/v1/pickable-champion-ids");
                    using var pchampJson = JsonDocument.Parse(await pchamp.Content.ReadAsStringAsync());
                    var dataElements = pchampJson.RootElement.EnumerateArray().Select(de => int.Parse(de.ToString()));
                    foreach (var i in dataElements)
                    {
                        if (pickChampId == i) validChampId = true;
                        if (i < 1 && i != -3) // special non-bravery champ id
                            Console.WriteLine($"there is a pickable special non-bravery(-3) champ with id: {i}.");
                    }
                    if (validChampId) await SelectChamp(id, pickChampId);
                }
                if (validChampId) await LockIn(id, pickChampId);
            }
        }
    }

    private static async Task SelectChamp(int actionId, int champId)
    {
        var payloadJson = JsonSerializer.Serialize(new { championId = champId });
        var content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
        var resp = await PatchAsync($"/lol-champ-select/v1/session/actions/{actionId}", content);
        if (!resp.IsSuccessStatusCode) Console.WriteLine("Failed select champ");
    }

    private static async Task LockIn(int actionId, int champId)
    {
        var payloadJson = JsonSerializer.Serialize(new { championId = champId, completed = true });
        var content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
        var resp = await PatchAsync($"/lol-champ-select/v1/session/actions/{actionId}", content);
        if (!resp.IsSuccessStatusCode) Console.WriteLine("Failed lock in");
    }

    private static async Task<string?> FindLockPath()
    {
        List<string> defaultPaths = [
            @"C:\Riot Games\League of Legends\lockfile",
            @"C:\Games\Riot Games\League of Legends\lockfile",
            @"C:\Program Files\Riot Games\League of Legends\lockfile",
            @"C:\Program Files (x86)\Riot Games\League of Legends\lockfile",
        ];
        foreach (var defaultPath in defaultPaths)
        {
            if (Path.Exists(defaultPath))
            {
                Console.WriteLine($"found lockfile path checking default paths. {defaultPath}");
                return defaultPath;
            }
        }
        foreach (var process in await Task.Run(Process.GetProcesses))
        {
            try
            {
                if (process.MainModule == null) continue;
                if (process.ProcessName.Contains("LeagueClient"))
                {
                    var path = process.MainModule.FileName.Replace(process.ProcessName + ".exe", "lockfile");
                    if (Path.Exists(path))
                    {
                        Console.WriteLine($"found lockfile path checking running processes. {path}");
                        return path;
                    }
                }
            }
            catch { }
        }
        return null;
    }

    private static async Task<string?> ReadLockfile(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            return await sr.ReadToEndAsync();
        }
        catch
        {
            return null;
        }
    }

    private static async Task ConnectToLeagueClient()
    {
        Console.WriteLine("Connecting to league client...");
        string? lockContent = null;
        while (true)
        {
            var lockPath = await FindLockPath();
            if (lockPath != null) lockContent = await ReadLockfile(lockPath);
            if (string.IsNullOrEmpty(lockContent))
            {
                Console.WriteLine("failed getting lockfile. retrying in 5.555 seconds... (open your client)");
                await Task.Delay(5555);
                continue;
            }
            else break;
        }
        var parts = lockContent.Split(':');
        if (parts.Length < 5 || !int.TryParse(parts[2], out port))
        {
            throw new Exception("Invalid lockfile format.");
        }
        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"riot:{parts[3]}"));
        client.DefaultRequestHeaders.Remove("Authorization");
        client.DefaultRequestHeaders.Add("Authorization", $"Basic {auth}");
        Console.WriteLine("successfully connected http client to league client");
        while (true) // wait for client to load in
        {
            if (await GetPhase() != null) break;
            await Task.Delay(1111);
        }
    }

    private static async Task<string?> GetPhase()
    {
        try
        {
            var resp = await GetAsync("/lol-gameflow/v1/gameflow-phase");
            return (await resp.Content.ReadAsStringAsync()).Trim('"');
        }
        catch
        {
            return null;
        }
    }

    private static async Task PopulateChampDictionary()
    {
        var resp = await client.GetAsync("https://ddragon.leagueoflegends.com/cdn/16.3.1/data/en_US/champion.json");
        using var respJsonDoc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var dataElement = respJsonDoc.RootElement.GetProperty("data");
        foreach (var champion in dataElement.EnumerateObject())
        {
            var id = champion.Value.GetProperty("key").GetString() ?? throw new Exception("bad champion key");
            var name = champion.Value.GetProperty("name").GetString()?.ToLower().Replace(" ", "") ?? throw new Exception("bad champion name");
            champions.Add(name, int.Parse(id));
        }
    }
}
