using System;
using System.Collections.Generic;
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
        readonly System.Diagnostics.Stopwatch stopWatch = new System.Diagnostics.Stopwatch();
        readonly Options options;
        readonly Random random = new Random();
        uint startCountdown = 0;
        bool initialized = false;

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
            // todo check player types
            // todo check player ids
            // todo check players count
            // todo make match observer an admin too
            // todo check players loaded status
            return true;
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
                if (startCountdown++ > 100)
                    Interop.uwAdminStartGame();
            }
            else
                startCountdown = 0;
        }

        void UpdateGame()
        {
            if (Game.Tick() > options.Duration * 20)
                Interop.uwAdminTerminateGame();
            // todo suggest camera
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
