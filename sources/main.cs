using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
        static readonly uint Invalid = 4294967295;
        readonly Stopwatch stopWatch = new Stopwatch();
        readonly Options options;
        readonly Random random = new Random();
        uint startCountdown = 0;
        long lastCameraUpdate = 0;
        uint? lastShotPosition = null;
        int lastPlayerIndex = 0;
        bool initialized = false;

        void Shooting(object sender, Interop.UwShootingData[] data)
        {
            lastShotPosition = data[0].shooter.position;
        }

        string PickMap()
        {
            var mapsList = options.Maps.ToList();
            if (mapsList.Count == 0)
                throw new InvalidOperationException("No maps.");
            int randomIndex = random.Next(mapsList.Count);
            return mapsList[randomIndex];
        }

        void Initialize()
        {
            for (int i = 0; i < options.Bots; i++)
                Interop.uwAdminAddAi();
            Interop.uwSendMapSelection(PickMap());
        }

        bool CheckPlayers()
        {
            bool result = true;
            var forces = new HashSet<uint>();
            ulong myUserId = Interop.uwGetUserId();

            var players = World.Entities().Values.Where(x => Entity.Has(x, "Player")).ToArray();
            foreach (var player in players)
            {
                uint id = player.Id;
                Interop.UwPlayerComponent p = player.Player;

                // check player type
                if (p.steamUserId != myUserId && p.force != Invalid)
                {
                    var expected = options.Uwapi ? Interop.UwPlayerConnectionClassEnum.UwApi : Interop.UwPlayerConnectionClassEnum.Computer;
                    if (p.playerConnectionClass != expected)
                    {
                        Interop.uwAdminKickPlayer(id);
                        result = false;
                        continue;
                    }
                }

                // check player id
                if (p.steamUserId != myUserId && options.Players.Count() > 0)
                {
                    if (!options.Players.Contains(id))
                    {
                        Interop.uwAdminKickPlayer(id);
                        result = false;
                        continue;
                    }
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

            return result;
        }

        void UpdateSession()
        {
            if (World.Entities().Count == 0)
                return;
            if (stopWatch.ElapsedMilliseconds > options.Timeout * 1000)
            {
                Console.WriteLine("timeout reached");
                Interop.uwAdminTerminateGame();
                return;
            }
            if (!initialized)
            {
                initialized = true;
                Initialize();
            }
            if (CheckPlayers())
            {
                if (startCountdown++ > Interop.UW_GameTicksPerSecond)
                    Interop.uwAdminStartGame();
            }
            else
                startCountdown = 0;
        }

        void SuggestCamera()
        {
            if (lastShotPosition != null)
            {
                Interop.uwSendCameraSuggestion(lastShotPosition.Value);
                lastShotPosition = null;
            }
            else
            {
                var players = World.Entities().Values.Where(x => Entity.Has(x, "ForceDetails")).ToArray();
                Debug.Assert(players.Count() > 0);
                lastPlayerIndex = (lastPlayerIndex + 1) % players.Count();
                Interop.uwSendCameraSuggestion(players[lastPlayerIndex].ForceDetails.startingPosition);
            }
        }

        void UpdateGame()
        {
            if (Game.Tick() > options.Duration * Interop.UW_GameTicksPerSecond)
                Interop.uwAdminTerminateGame();
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
            Console.WriteLine("starting");
            Game.ConnectNewServer(options.Visibility, options.Name, "--allowUwApiAdmin 1");
            Console.WriteLine("done");
        }

        MatchAdmin(Options options_)
        {
            options = options_;
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

            MatchAdmin admin = new MatchAdmin(options.Value);
            admin.Start();
            return 0;
        }
    }
}
