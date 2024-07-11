using System;

namespace Unnatural
{
    internal class MatchAdmin
    {
        readonly Random random = new Random();

        void Updating(object sender, bool stepping)
        {
            if (!stepping)
                return;
            // todo
        }

        void Start()
        {
            Game.SetPlayerName("match-admin");
            Game.SetStartGui(true);
            Game.ConnectNewServer();
        }

        MatchAdmin()
        {
            Game.Updating += Updating;
        }

        static void Main(string[] args)
        {
            MatchAdmin admin = new MatchAdmin();
            admin.Start();
        }
    }
}
