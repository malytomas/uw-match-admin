using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using CommandLine;

namespace Unnatural
{
    class Options
    {
        [Option('p', "players", Required = false, Separator = ' ', HelpText = "Steam IDs of players (or leave empty for any players). Use spaces to separate multiple ids. Use underscore to separate teams. (If no teams are defined the game will be treated as FFA.)")]
        public IEnumerable<string> PlayersAndTeams { get; set; }

        [Option('m', "maps", Required = true, Separator = ' ', HelpText = "Paths of maps (one will be chosen randomly).")]
        public IEnumerable<string> Maps { get; set; }

        [Option('u', "uwapi", Default = false, Required = false, HelpText = "Allow UwApi players only.")]
        public bool Uwapi { get; set; }

        [Option('v', "visibility", Default = (uint)0, Required = false, HelpText = "Server visibility (0 = localhost, 1 = LAN, 2 = private, 3 = friends, 4 = public)")]
        public uint Visibility { get; set; }

        [Option('n', "name", Default = "match", Required = false, HelpText = "Match server name.")]
        public string Name { get; set; }

        [Option('d', "duration", Default = (uint)3600, Required = false, HelpText = "Max duration of the match (in-game seconds).")]
        public uint Duration { get; set; }

        [Option('t', "timeout", Default = (uint)300, Required = false, HelpText = "Timeout to start the game (seconds).")]
        public uint Timeout { get; set; }

        [Option('o', "observer", Default = true, Required = false, HelpText = "Start local observer.")]
        public bool? Observer { get; set; }

        [Option('a', "announcement", Default = false, Required = false, HelpText = "Announce lobby id and list of players to http-server.")]
        public bool? Announcement { get; set; }

        [Option('b', "bots", Default = (uint)0, Required = false, HelpText = "Number of built-in AI players to add to the game.")]
        public uint Bots { get; set; }

        public List<ulong> ExtractPlayers()
        {
            List<ulong> ps = new List<ulong>();
            foreach (string p in PlayersAndTeams)
            {
                ulong u;
                if (ulong.TryParse(p, out u))
                    ps.Add(u);
            }
            return ps;
        }

        public List<List<ulong>> ExtractTeams()
        {
            List<List<ulong>> teams = new List<List<ulong>>();
            List<ulong> t = new List<ulong>();
            foreach (string p in PlayersAndTeams)
            {
                ulong u;
                if (ulong.TryParse(p, out u))
                    t.Add(u);
                else if (p == "_")
                {
                    if (t.Count > 0)
                    {
                        teams.Add(t);
                        t = new List<ulong>();
                    }
                }
                else
                {
                    Console.WriteLine(">" + p + "<");
                    throw new Exception("parameter is not a steam id nor team separator");
                }
            }
            if (t.Count > 0)
                teams.Add(t);

            if (teams.Count > 1)
                return teams;

            if (teams.Count == 0)
                return teams;

            teams = new List<List<ulong>>();
            foreach (string p in PlayersAndTeams)
            {
                ulong u;
                if (ulong.TryParse(p, out u))
                {
                    t = new List<ulong>();
                    t.Add(u);
                    teams.Add(t);
                }
            }
            return teams;
        }
    }

    class ColorConverter
    {
        public static void HsvToRgb(float h, float s, float v, out float r, out float g, out float b)
        {
            float c = v * s; // Chroma
            float x = c * (1 - Math.Abs((h * 6) % 2 - 1)); // Intermediate value
            float m = v - c;

            float r1, g1, b1;

            if (h < 1.0 / 6.0)
            {
                r1 = c;
                g1 = x;
                b1 = 0;
            }
            else if (h < 2.0 / 6.0)
            {
                r1 = x;
                g1 = c;
                b1 = 0;
            }
            else if (h < 3.0 / 6.0)
            {
                r1 = 0;
                g1 = c;
                b1 = x;
            }
            else if (h < 4.0 / 6.0)
            {
                r1 = 0;
                g1 = x;
                b1 = c;
            }
            else if (h < 5.0 / 6.0)
            {
                r1 = x;
                g1 = 0;
                b1 = c;
            }
            else
            {
                r1 = c;
                g1 = 0;
                b1 = x;
            }

            // Adding m to match the lightness
            r = r1 + m;
            g = g1 + m;
            b = b1 + m;
        }
    }

    class MatchAdmin
    {
        const uint Invalid = 4294967295;
        readonly Stopwatch stopWatch = new Stopwatch();
        readonly Options options;
        readonly List<ulong> players;
        readonly List<List<ulong>> teams;
        readonly Random random = new Random();
        uint startCountdown = 0;
        bool initialized = false;
        readonly string publishLobbyBaseUrl;
        Task<HttpResponseMessage> publishLobbyTask;

        string PickMap()
        {
            var mapsList = options.Maps.ToList();
            if (mapsList.Count == 0)
                throw new Exception("no maps");
            int randomIndex = random.Next(mapsList.Count);
            return mapsList[randomIndex];
        }

        void PublishLobby()
        {
            Debug.Assert(publishLobbyBaseUrl.Length > 0);
            Interop.uwLog(Interop.UwSeverityEnum.Info, "publishing lobby id");
            string url = publishLobbyBaseUrl + "/api/publish_lobby";
            string ps = players.Count == 0 ? "[]" : "[\"" + string.Join("\",\"", players.Select(x => x.ToString())) + "\"]";
            string data = "{\"lobby_id\":\"" + Interop.uwGetLobbyId() + "\",\"server_address\":\"" + (Environment.GetEnvironmentVariable("UNNATURAL_MY_ADDR") ?? "") + "\",\"server_port\":\"" + Interop.uwGetServerPort() + "\",\"steam_ids\": " + ps + "}";
            HttpContent content = new StringContent(data, Encoding.UTF8, "application/json");
            HttpClient client = new HttpClient();
            client.DefaultRequestHeaders.Add("Authorization", "Bearer admin");
            publishLobbyTask = client.PostAsync(url, content);
        }

        bool CheckLobbyPublication()
        {
            if (publishLobbyTask == null)
                return true;
            switch (publishLobbyTask.Status)
            {
                case TaskStatus.Canceled:
                case TaskStatus.Faulted:
                    Game.LogError("failed to publish lobby id");
                    var ex = publishLobbyTask.Exception ?? new Exception("failed to publish lobby id");
                    publishLobbyTask = null;
                    throw ex;
                case TaskStatus.RanToCompletion:
                    var response = publishLobbyTask.Result;
                    Game.LogInfo("received response from http server, code: " + response.StatusCode);
                    publishLobbyTask = null;
                    if (!response.IsSuccessStatusCode)
                        throw new Exception("failed lobby task publish");
                    return true;
                default:
                    return false;
            }
        }

        void Initialize()
        {
            Game.LogInfo("initializing");
            Game.SetPlayerName("match-admin");
            for (int i = 0; i < options.Bots; i++)
                Admin.AddAi();
            string map = PickMap();
            Game.LogInfo("chosen map: " + map);
            Admin.SetMapSelection(map);
            Admin.SetAutomaticSuggestedCameraFocus(true);
            if (options.Announcement.Value)
                PublishLobby();
        }

        bool CheckPlayers()
        {
            bool result = true;
            var forces = new HashSet<uint>();
            var playerIds = new HashSet<ulong>();
            ulong myUserId = Admin.GetUserId();

            foreach (var player in World.Entities().Values.Where(x => x.Player.HasValue))
            {
                uint id = player.Id;
                Interop.UwPlayerComponent p = player.Player.Value;

                // check player connection class
                if (p.steamUserId != myUserId && p.force != Invalid)
                {
                    var expected = options.Uwapi ? Interop.UwPlayerConnectionClassEnum.UwApi : Interop.UwPlayerConnectionClassEnum.Computer;
                    if (p.playerConnectionClass != expected)
                    {
                        Game.LogInfo("kicking player - forbidden connection class");
                        Admin.KickPlayer(id);
                        result = false;
                    }
                }

                // check permitted steam user id
                if (p.steamUserId != myUserId && players.Count > 0)
                {
                    if (!players.Contains(p.steamUserId))
                    {
                        Game.LogInfo("kicking player - forbidden steam user id");
                        Admin.KickPlayer(id);
                        result = false;
                    }
                }

                // check duplicate steam user id
                if (p.steamUserId != myUserId && p.force != Invalid)
                {
                    if (playerIds.Contains(p.steamUserId))
                        result = false;
                    else
                        playerIds.Add(p.steamUserId);
                }

                // check one player per force
                if (p.force != Invalid)
                {
                    if (forces.Contains(p.force))
                        result = false;
                    else
                        forces.Add(p.force);
                }

                // check loaded
                if ((p.state & Interop.UwPlayerStateFlags.Loaded) == 0)
                    result = false;

                // check match observer is admin
                if (p.steamUserId == myUserId && p.playerConnectionClass == Interop.UwPlayerConnectionClassEnum.Computer && (p.state & Interop.UwPlayerStateFlags.Admin) == 0)
                    Admin.PlayerSetAdmin(id, true);
            }

            // check map is overcrowded
            if (forces.Count > Map.MaxPlayers())
                result = false;

            // check map is filled
            if (players.Count == 0 && (stopWatch.ElapsedMilliseconds < options.Timeout * 1000))
            {
                if (forces.Count < Map.MaxPlayers())
                    result = false;
            }

            // check all players present
            if (players.Count > 0)
            {
                if (!playerIds.SetEquals(players))
                    result = false;
            }

            // teams
            if (players.Count > 0)
            {
                var playerToTeam = new Dictionary<ulong, uint>();
                var playerToForce = new Dictionary<ulong, uint>();
                foreach (var player in World.Entities().Values.Where(x => x.Player.HasValue))
                {
                    ulong sid = player.Player.Value.steamUserId;
                    uint force = player.Player.Value.force;
                    if (force == 0 || force == Invalid)
                        continue;
                    uint team = World.Entity(force).Force.Value.intendedTeam;
                    playerToTeam.Add(sid, team);
                    playerToForce.Add(sid, force);
                }
                foreach (var ps in teams)
                {
                    ulong primary = ps[0];
                    if (!playerToTeam.ContainsKey(primary))
                        continue;
                    uint t = playerToTeam[primary];
                    foreach (ulong p in ps)
                    {
                        if (!playerToTeam.ContainsKey(p))
                            continue;
                        if (playerToTeam[p] != t)
                        {
                            result = false;
                            Admin.ForceJoinTeam(playerToForce[p], t);
                        }
                    }
                }
            }

            { // colors
                int i = 0;
                foreach (uint force in forces)
                {
                    float h = (float)i / (float)forces.Count;
                    float r, g, b;
                    ColorConverter.HsvToRgb(h, 1, 1, out r, out g, out b);
                    Admin.ForceSetColor(force, r, g, b);
                    i++;
                }
            }

            return result;
        }

        void UpdateSession()
        {
            if (!World.IsAdmin())
            {
                Game.LogWarning("not admin (yet)");
                return;
            }
            if (stopWatch.ElapsedMilliseconds > options.Timeout * 1000 + 5000)
            {
                Game.LogError("session timeout reached");
                throw new Exception("session timed out");
            }
            if (!initialized)
            {
                initialized = true;
                Initialize();
            }
            if (!CheckLobbyPublication())
                return;
            if (CheckPlayers())
            {
                if (startCountdown++ > Interop.UW_GameTicksPerSecond)
                {
                    Game.LogInfo("starting game");
                    Admin.StartGame();
                    startCountdown = 0;
                }
            }
            else
                startCountdown = 0;
        }

        void UpdateGame()
        {
            if (Game.GameTick() > options.Duration * Interop.UW_GameTicksPerSecond)
            {
                Game.LogError("game max duration reached");
                Admin.TerminateGame();
            }
        }

        void Updating(object sender, bool stepping)
        {
            switch (Game.GameState())
            {
                case Interop.UwGameStateEnum.Session:
                    UpdateSession();
                    break;
                case Interop.UwGameStateEnum.Game:
                    UpdateGame();
                    break;
            }
        }

        void Start()
        {
            stopWatch.Start();
            Game.SetConnectStartGui(options.Observer.Value, "--observer 2 --name match-observer");
            Game.LogInfo("starting");
            Game.ConnectNewServer(options.Visibility, options.Name, "--allowUwApiAdmin 1");
            Game.LogInfo("done");
        }

        MatchAdmin(Options options_, string publishLobbyBaseUrl_)
        {
            options = options_;
            players = options_.ExtractPlayers();
            teams = options_.ExtractTeams();
            publishLobbyBaseUrl = publishLobbyBaseUrl_;
            Events.Updating += Updating;
        }

        static int Main(string[] args)
        {
            LibraryHelpers.SetCurrentDirectory();

            var options = Parser.Default.ParseArguments<Options>(args);
            if (options.Tag == ParserResultType.NotParsed)
            {
                Console.Error.WriteLine("Failed parsing options.");
                return 1;
            }

            string publishLobbyBaseUrl = "";
            if (options.Value.Announcement.Value)
            {
                publishLobbyBaseUrl = Environment.GetEnvironmentVariable("UNNATURAL_URL"); ;
                if (publishLobbyBaseUrl == null)
                {
                    Console.Error.WriteLine("Environment variable UNNATURAL_URL must be set.");
                    return 2;
                }
            }

            MatchAdmin admin = new MatchAdmin(options.Value, publishLobbyBaseUrl);
            admin.Start();
            return 0;
        }
    }
}
