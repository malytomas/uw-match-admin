using System;
using System.Collections.Generic;
using System.Linq;
using CommandLine;

namespace Unnatural
{
    class Options
    {
        //[Option('p', "players", Required = false, Separator = ' ', HelpText = "Steam IDs of players (or leave empty for any players).")]
        //public IEnumerable<ulong> Players { get; set; }

        [Option('m', "maps", Required = true, Separator = ' ', HelpText = "Paths of maps (one will be chosen randomly).")]
        public IEnumerable<string> Maps { get; set; }

        //[Option('v', "visibility", Default = (uint)2, Required = false, HelpText = "Server visibility (0 = localhost, 1 = LAN, 2 = private, 3 = friends, 4 = public)")]
        //public uint Visibility { get; set; }

        [Option('o', "observer", Default = true, Required = false, HelpText = "Start local observer.")]
        public bool? Observer { get; set; }

        [Option('b', "bots", Default = (uint)0, Required = false, HelpText = "Number of built-in AI players to add to the game.")]
        public uint Bots { get; set; }
    }

    internal class MatchAdmin
    {
        readonly Options options;
        readonly Random random = new Random();

        string pickMap()
        {
            var mapsList = options.Maps.ToList();
            if (mapsList.Count == 0)
                throw new InvalidOperationException("No maps.");
            int randomIndex = random.Next(mapsList.Count);
            return mapsList[randomIndex];
        }

        void UpdateSession()
        {
            if (World.Entities().Count == 0)
                return;
            {
                for (int i = 0; i < options.Bots; i++)
                    Interop.uwAdminAddAi();
                options.Bots = 0;
            }
        }

        void UpdateGame()
        {
            // todo
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
            Game.SetPlayerName("match-admin");
            Game.SetPlayerColor(0, 0, 0);
            Interop.uwSetConnectAsObserver(true);
            Game.SetStartGui(options.Observer.Value, "--observer 1");
            //Game.ConnectNewServer("--allowUwApiAdmin 1 --visibility " + options.Visibility.ToString() + " --map " + pickMap());
            Game.ConnectNewServer("--allowUwApiAdmin 1 --map " + pickMap());
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
            //Console.WriteLine("players: " + string.Join(" ", options.Value.Players));
            Console.WriteLine("maps: " + string.Join(" ", options.Value.Maps));
            //Console.WriteLine("visibility: " + options.Value.Visibility.ToString());
            Console.WriteLine("observer: " + options.Value.Observer.ToString());
            Console.WriteLine("bots: " + options.Value.Bots.ToString());

            MatchAdmin admin = new MatchAdmin(options.Value);
            admin.Start();
            return 0;
        }
    }
}
