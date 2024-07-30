using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using CommandLine;

namespace Unnatural
{
    class Options
    {
        [Option('p', "players", Required = false, Separator = ' ', HelpText = "Steam IDs of players (or leave empty for any players).")]
        public IEnumerable<ulong> Players { get; set; }

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

        [Option('b', "bots", Default = (uint)0, Required = false, HelpText = "Number of built-in AI players to add to the game.")]
        public uint Bots { get; set; }
    }

    class MatchAdmin
    {
        const uint Invalid = 4294967295;
        readonly Stopwatch stopWatch = new Stopwatch();
        readonly Options options;
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
                throw new InvalidOperationException("no maps");
            int randomIndex = random.Next(mapsList.Count);
            return mapsList[randomIndex];
        }

        void PublishLobby()
        {
            var url = publishLobbyBaseUrl + "/api/publish_lobby?lobby_id=" + Interop.uwGetLobbyId() + "&players=" + string.Join(",", options.Players.Select(x => x.ToString()));
            var c = new HttpClient();
            publishLobbyTask = c.GetAsync(url);
        }

        bool CheckLobbyPublication()
        {
            switch (publishLobbyTask.Status)
            {
                case TaskStatus.Canceled:
                case TaskStatus.Faulted:
                    Interop.uwLog(Interop.UwSeverityEnum.Error, "failed to publish lobby id");
                    Interop.uwAdminTerminateGame();
                    throw publishLobbyTask.Exception ?? new Exception("failed to publish lobby id");
                case TaskStatus.RanToCompletion:
                    return true;
                default:
                    return false;
            }
        }

        void Initialize()
        {
            for (int i = 0; i < options.Bots; i++)
                Interop.uwAdminAddAi();
            string map = PickMap();
            Interop.uwLog(Interop.UwSeverityEnum.Info, "chosen map: " + map);
            Interop.uwSendMapSelection(map);
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
                if (p.steamUserId != myUserId && options.Players.Count() > 0)
                {
                    if (!options.Players.Contains(p.steamUserId))
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
            if (options.Players.Count() > 0)
            {
                if (!playerIds.SetEquals(options.Players))
                    result = false;
            }

            // check map is filled
            if (options.Players.Count() == 0)
            {
                if (forces.Count() < Map.MaxPlayers())
                    result = false;
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
                    Interop.uwLog(Interop.UwSeverityEnum.Warning, "not admin (yet)");
                    return;
                }
            }
            if (stopWatch.ElapsedMilliseconds > options.Timeout * 1000)
            {
                Interop.uwLog(Interop.UwSeverityEnum.Error, "session timeout reached");
                Interop.uwAdminTerminateGame();
                return;
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
                Interop.uwLog(Interop.UwSeverityEnum.Error, "game max duration reached");
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

            var options = Parser.Default.ParseArguments<Options>(args);
            if (options.Tag == ParserResultType.NotParsed)
                return 2;

            string publishLobbyBaseUrl = Environment.GetEnvironmentVariable("UNNATURAL_HTTP_URL");
            if (publishLobbyBaseUrl == null)
                publishLobbyBaseUrl = "http://127.0.0.1/";

            MatchAdmin admin = new MatchAdmin(options.Value, publishLobbyBaseUrl);
            admin.Start();
            return 0;
        }
    }
}
