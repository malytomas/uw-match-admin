using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Media;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
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

        [Option('t', "timeout", Default = (uint)900, Required = false, HelpText = "Timeout to start the game (seconds).")]
        public uint Timeout { get; set; }

        [Option('o', "observer", Default = true, Required = false, HelpText = "Start local observer.")]
        public bool? Observer { get; set; }

        [Option('a', "anouncement", Default = false, Required = false, HelpText = "Anounce lobby id and list of players to http-server.")]
        public bool? Anouncement { get; set; }

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
            {
                teams.Add(t);
                t = new List<ulong>();
            }

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

        long lastCameraUpdate = 0;
        readonly Dictionary<uint, uint> forcesShootingPosition = new Dictionary<uint, uint>();
        readonly Dictionary<uint, uint> forcesBuildingPosition = new Dictionary<uint, uint>();
        int lastShootingForceIndex = 0;
        int lastBuildingForceIndex = 0;

        void Shooting(object sender, Interop.UwShootingData[] data)
        {
            foreach (var it in data)
            {
                forcesShootingPosition.Remove(it.shooter.force);
                forcesShootingPosition.Add(it.shooter.force, it.shooter.position);
            }
        }

        static bool IsNewBuilding(dynamic x)
        {
            if (!Entity.Has(x, "Unit"))
                return false;
            if (Entity.Has(x, "Visited"))
                return false;
            uint p = x.Proto.proto;
            return Prototypes.Type(p) == Interop.UwPrototypeTypeEnum.Unit && Prototypes.Unit(p).buildingRadius > 0;
        }

        void Buildings()
        {
            foreach (var b in World.Entities().Values.Where(x => IsNewBuilding(x)))
            {
                uint o = b.Owner.force;
                forcesBuildingPosition.Remove(o);
                forcesBuildingPosition.Add(o, b.Position.position);
                b.Visited = true;
            }
        }

        void SuggestCamera()
        {
            var forces = World.Entities().Values.Where(x => Entity.Has(x, "Force") && (x.Force.state & Interop.UwForceStateFlags.Defeated) == 0).Select(x => (uint)x.Id).ToList();
            if (forces.Count() == 0)
                return;

            // shooting
            forcesShootingPosition.Keys.Where(key => !forces.Contains(key)).ToList().ForEach(key => forcesShootingPosition.Remove(key));
            if (forcesShootingPosition.Count() > 0)
            {
                lastShootingForceIndex = (lastShootingForceIndex + 1) % forces.Count();
                while (!forcesShootingPosition.ContainsKey(forces[lastShootingForceIndex]))
                    lastShootingForceIndex = (lastShootingForceIndex + 1) % forces.Count();
                Interop.uwSendCameraSuggestion(forcesShootingPosition[forces[lastShootingForceIndex]]);
                forcesShootingPosition.Clear();
                return;
            }

            // buildings
            lastBuildingForceIndex = (lastBuildingForceIndex + 1) % forces.Count();
            Interop.uwSendCameraSuggestion(forcesBuildingPosition[forces[lastBuildingForceIndex]]);
        }

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
            Interop.uwLog(Interop.UwSeverityEnum.Info, "publishing lobby id");
            string url = publishLobbyBaseUrl + "/api/publish_lobby";
            string data = "{\"lobby_id\":\"" + Interop.uwGetLobbyId() + "\",\"steam_ids\": [\"" + string.Join("\",\"", players.Select(x => x.ToString())) + "\"]}";
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
                    throw publishLobbyTask.Exception ?? new Exception("failed to publish lobby id");
                case TaskStatus.RanToCompletion:
                    var response = publishLobbyTask.Result;
                    Game.LogInfo("received response from http server, code: " + response.StatusCode);
                    if (!response.IsSuccessStatusCode)
                        throw new Exception("failed lobby task publish");
                    publishLobbyTask = null;
                    return true;
                default:
                    return false;
            }
        }

        void Initialize()
        {
            Interop.uwLog(Interop.UwSeverityEnum.Info, "initializing");
            for (int i = 0; i < options.Bots; i++)
                Interop.uwAdminAddAi();
            string map = PickMap();
            Interop.uwLog(Interop.UwSeverityEnum.Info, "chosen map: " + map);
            Interop.uwSendMapSelection(map);
            if (options.Anouncement.Value)
                PublishLobby();
        }

        bool CheckPlayers()
        {
            bool result = true;
            var forces = new HashSet<uint>();
            var playerIds = new HashSet<ulong>();
            ulong myUserId = Interop.uwGetUserId();

            foreach (var player in World.Entities().Values.Where(x => Entity.Has(x, "Player")))
            {
                uint id = player.Id;
                Interop.UwPlayerComponent p = player.Player;

                // check player type
                if (p.steamUserId != myUserId && p.force != Invalid)
                {
                    var expected = options.Uwapi ? Interop.UwPlayerConnectionClassEnum.UwApi : Interop.UwPlayerConnectionClassEnum.Computer;
                    if (p.playerConnectionClass != expected)
                    {
                        Interop.uwLog(Interop.UwSeverityEnum.Info, "kicking player - wrong type");
                        Interop.uwAdminKickPlayer(id);
                        result = false;
                    }
                }

                // check allowed user id
                if (p.steamUserId != myUserId && players.Count() > 0)
                {
                    if (!players.Contains(p.steamUserId))
                    {
                        Interop.uwLog(Interop.UwSeverityEnum.Info, "kicking player - wrong id");
                        Interop.uwAdminKickPlayer(id);
                        result = false;
                    }
                }

                // check duplicate user id
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

                // check match observer
                if (p.steamUserId == myUserId && p.playerConnectionClass == Interop.UwPlayerConnectionClassEnum.Computer && (p.state & Interop.UwPlayerStateFlags.Admin) == 0)
                    Interop.uwAdminPlayerSetAdmin(id, true);
            }

            // check forces count
            if (forces.Count() > Map.MaxPlayers())
                result = false;

            // check all players present
            if (players.Count() > 0)
            {
                if (!playerIds.SetEquals(players))
                    result = false;
            }

            // check map is filled
            if (players.Count() == 0)
            {
                if (forces.Count() < Map.MaxPlayers())
                    result = false;
            }

            return result;
        }

        bool CheckTeams()
        {
            if (players.Count == 0)
                return true;

            bool result = true;

            var playerToTeam = new Dictionary<ulong, uint>();
            var playerToForce = new Dictionary<ulong, uint>();
            foreach (var player in World.Entities().Values.Where(x => Entity.Has(x, "Player")))
            {
                ulong sid = player.Player.steamUserId;
                uint force = player.Player.force;
                if (force == 0 || force == Invalid)
                    continue;
                uint team = World.Entity(force).Force.team;
                playerToTeam.Add(sid, team);
                playerToForce.Add(sid, force);
            }

            foreach (var ps in teams)
            {
                uint t = playerToTeam[ps[0]];
                foreach (ulong p in ps)
                {
                    if (playerToTeam[p] != t)
                    {
                        result = false;
                        Interop.uwAdminForceJoinTeam(playerToForce[p], t);
                    }
                }
            }

            return result;
        }

        void UpdateSession()
        {
            {
                var mp = new Interop.UwMyPlayer();
                if (!Interop.uwMyPlayer(ref mp))
                    return;
                if (!mp.admin)
                {
                    Game.LogWarning("not admin (yet)");
                    return;
                }
            }
            if (stopWatch.ElapsedMilliseconds > options.Timeout * 1000)
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
            if (CheckPlayers() && CheckTeams())
            {
                if (startCountdown++ > Interop.UW_GameTicksPerSecond)
                {
                    Interop.uwLog(Interop.UwSeverityEnum.Info, "starting game");
                    Interop.uwAdminStartGame();
                    startCountdown = 0;
                }
            }
            else
                startCountdown = 0;
        }

        void UpdateGame()
        {
            if (Game.Tick() > options.Duration * Interop.UW_GameTicksPerSecond)
            {
                Game.LogError("game max duration reached");
                Interop.uwAdminTerminateGame();
                return;
            }

            Buildings();
            long current = stopWatch.ElapsedMilliseconds;
            if (current > lastCameraUpdate + 5000)
            {
                lastCameraUpdate = current;
                SuggestCamera();
            }
        }

        void Updating(object sender, bool stepping)
        {
            if (Game.GameState() == Interop.UwGameStateEnum.Session)
            {
                UpdateSession();
                return;
            }
            if (Game.GameState() == Interop.UwGameStateEnum.Game)
            {
                if (stepping)
                    UpdateGame();
                return;
            }
        }

        void Start()
        {
            stopWatch.Start();
            Game.SetPlayerName("match-admin");
            Game.SetPlayerColor(0, 0, 0);
            Interop.uwSetConnectAsObserver(true);
            Game.SetStartGui(options.Observer.Value, "--observer 2 --name match-observer");
            Interop.uwLog(Interop.UwSeverityEnum.Info, "starting");
            Game.ConnectNewServer(options.Visibility, options.Name, "--allowUwApiAdmin 1");
            Interop.uwLog(Interop.UwSeverityEnum.Info, "done");
        }

        MatchAdmin(Options options_, string publishLobbyBaseUrl_)
        {
            options = options_;
            players = options_.ExtractPlayers();
            teams = options_.ExtractTeams();
            publishLobbyBaseUrl = publishLobbyBaseUrl_;
            Game.Updating += Updating;
            Game.Shooting += Shooting;
        }

        static int Main(string[] args)
        {
            string root = Environment.GetEnvironmentVariable("UNNATURAL_ROOT");
            if (root == null)
            {
                Console.Error.WriteLine("Environment variable UNNATURAL_ROOT must be set.");
                Console.Error.WriteLine("Eg. <steam path>/steamapps/common/Unnatural Worlds/bin.");
                return 1;
            }
            System.IO.Directory.SetCurrentDirectory(root);

            string publishLobbyBaseUrl = Environment.GetEnvironmentVariable("UNNATURAL_URL");
            if (publishLobbyBaseUrl == null)
            {
                Console.Error.WriteLine("Environment variable UNNATURAL_URL must be set.");
                return 1;
            }

            var options = Parser.Default.ParseArguments<Options>(args);
            if (options.Tag == ParserResultType.NotParsed)
            {
                Console.Error.WriteLine("Failed parsing options.");
                return 2;
            }

            /*
            { // debug print of parsed teams
                var teams = options.Value.ExtractTeams();
                Console.WriteLine(string.Join(" | ", teams.Select(team => string.Join(" ", team))));
                return 0;
            }
            */

            try
            {
                MatchAdmin admin = new MatchAdmin(options.Value, publishLobbyBaseUrl);
                admin.Start();
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                Interop.uwAdminTerminateGame();
                throw;
            }
            return 0;
        }
    }
}
