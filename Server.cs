using System;
using System.IO;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Text;

namespace AnimalShogi
{
    public class Player
    {
        public Player(TcpClient c, NetworkStream n, int i)
        {
            this.tcp = c;
            this.stream = n;
            this.pID = i;
        }

        public TcpClient Tcp() {
            return tcp;
        }

        public NetworkStream Stream() {
            return stream;
        }

        public Player Opponent() {
            return  opponent;
        }

        public int GameId() {
            return gID;
        }

        public bool FirstPlayer() {
            return firstPlayer;
        }

        public int PlayerId() {
            return pID;
        }

        public Color MyColor() {
            return color;
        }

        public bool Waiting() {
            return waiting;
        }

        public void SetOpponent(Player opp) {
            opponent = opp;
        }

        public void SetPlayerId(int id) {
            pID = id;
        }

        public void SetGameId(int id) {
            gID = id;
        }

        public void SetFirstPlayer(bool b) {
            firstPlayer = b;
        }

        public void SetColor(Color c) {
            color = c;
        }

        public void SetWaiting(bool w) {
            waiting = w;
        }

        private TcpClient tcp;
        private NetworkStream stream;
        private Player opponent;
        private int pID;
        private int gID;
        private bool firstPlayer;
        private bool waiting;

        private Color color;
    }

    public class Game
    {
        public Game(Player one, Player two, int i)
        {
            this.pos = new Position();
            this.player1 = one;
            this.player2 = two;
            this.id = i;
        }

        public Player Player1() {
            return player1;
        }

        public Player Player2() {
            return player2;
        }

        public int Id() {
            return id;
        }

        public Position pos;

        private Player player1;
        private Player player2;
        private int id;
    }

    public class Server
    {
        static List<Game> games = new List<Game>();
        static List<Player> players = new List<Player>();
        static readonly object gamesLock = new object();
        TcpListener listener;
        Thread clientThread;
        TcpClient client;
        NetworkStream nwStream;
        static Player waitingPlayer = null;
        static readonly object waitingPlayerLock = new object();
        int playerID = 1;
        int gameID = 1;

        public Server(string port)
        {
            int portInt;
            string portString = port;
            bool good = true;

            do {
                while(Int32.TryParse(portString, out portInt) == false) 
                {
                    Console.WriteLine("Enter port");
                    portString = Console.ReadLine();
                }

                try {
                    IPAddress address = IPAddress.Any;
                    listener = new TcpListener(address, portInt);
                    listener.Start();
                    Console.WriteLine("Setup is successful. Waiting for clients");
                    MakeHtml();
                    matchMaking();
                } catch(Exception e) {
                    good = false;
                    Console.WriteLine(e.Message);
                    Console.WriteLine("Try again");
                }
            } while (good == false);
        }

        private void WriteStream(NetworkStream stream, string message)
        {
            byte[] data = Encoding.GetEncoding("UTF-8").GetBytes(message);
            stream.Write(data, 0, data.Length);
        }

        private void SendGameSummary(NetworkStream stream, Color c)
        {
            WriteStream(stream, "BEGIN Game_Summary\n");
            WriteStream(stream, "Game_ID:" + DateTime.Now.ToString("yyyyMMdd-HH-mm-ss") + "\n");
            WriteStream(stream, "Your_Turn:" + (c == Color.BLACK ? "+" : "-") + "\n");
            WriteStream(stream, "END Game_Summary\n");
        }

        private void matchMaking()
        {
            while (true) //Server main loop
            {
                // wait for clients to connect
                client = listener.AcceptTcpClient();
                nwStream = client.GetStream();
                Player newPlayer = new Player(client, nwStream, playerID++);
                players.Add(newPlayer);
                clientThread = new Thread(new ParameterizedThreadStart(clientComm));
                clientThread.Start(newPlayer);
                addPlayer(newPlayer);
            }
        }

        private void addPlayer(Player nPlayer)
        {
            Console.WriteLine("Player #" + nPlayer.PlayerId() + " joined");

            lock (waitingPlayerLock)
            {
                if (waitingPlayer == null)
                {
                    waitingPlayer = nPlayer;
                }
                else
                {
                    waitingPlayer.SetFirstPlayer(true);
                    nPlayer.SetFirstPlayer(false);
                    waitingPlayer.SetGameId(gameID);
                    nPlayer.SetGameId(gameID);
                    waitingPlayer.SetOpponent(nPlayer);
                    nPlayer.SetOpponent(waitingPlayer);

                    // 先後をランダムに決める
                    int t = new Random().Next(10000);
                    if (t % 2 == 0) {
                        nPlayer.SetColor(Color.BLACK);
                        waitingPlayer.SetColor(Color.WHITE);
                    }
                    else {
                        nPlayer.SetColor(Color.WHITE);
                        waitingPlayer.SetColor(Color.BLACK);
                    }

                    lock (gamesLock)
                    {
                        games.Add(new Game(waitingPlayer, nPlayer, gameID));
                    }

                    //Tell clients to start game
                    SendGameSummary(waitingPlayer.Stream(), waitingPlayer.MyColor());
                    SendGameSummary(nPlayer.Stream(), nPlayer.MyColor());
                    Console.WriteLine("Started game #" + gameID + ", with player #" + waitingPlayer.PlayerId() + " and player #" + nPlayer.PlayerId());
                    waitingPlayer = null;
                    gameID++;
                }
            }
        }

        private void clientComm(object p)
        {
            bool isready = false;
            int bytesRead;
            Player threadPlayer = (Player)p;
            TcpClient threadClient = threadPlayer.Tcp();
            NetworkStream threadStream = threadPlayer.Stream();
            byte[] buffer = new byte[255];

            while(true)
            {
                // wait for data to come in
                try
                {
                    bytesRead = threadStream.Read(buffer, 0, 255);
                    string bufferStr = Encoding.UTF8.GetString(buffer);

                    Console.WriteLine(bufferStr);

                    // if client clicks cancel
                    if (bytesRead == 0)
                    {
                        if ( threadPlayer.Opponent() != null
                          && threadPlayer.Opponent().Tcp().Connected == true)
                        {
                            // tell opponent game ended
                            WriteStream(threadPlayer.Opponent().Stream(), "#ABNORMAL\n");
                        }
                        break;
                    }

                    if (!isready && bufferStr.StartsWith("AGREE")) {
                        WriteStream(threadPlayer.Stream(), "START\n");
                        isready = true;
                        continue;
                    }

                    if (   threadClient.Client.Connected
                        && threadClient.Client.Poll(1000, SelectMode.SelectRead) 
                        && (threadClient.Client.Available == 0))
                        break;

                    if (threadPlayer.Opponent() == null)
                        continue;

                    // resign
                    if (bufferStr.StartsWith("resign"))
                    {
                        WriteStream(threadPlayer.Opponent().Stream(), "#GAME_OVER\n");
                        WriteStream(threadPlayer.Opponent().Stream(), "#WIN\n");
                        break;
                    }
                    else if (bufferStr.StartsWith("+") || (bufferStr.StartsWith("-"))) 
                    {
                        // 処理中に対局が無くなると死ぬ
                        int gameIdx = -1;
                        for (int i = 0; i < games.Count; i++)
                            if (games[i].Id() == threadPlayer.GameId())
                                gameIdx = i;

                        if (gameIdx == -1)
                        {
                            Console.WriteLine("ERROR");
                            break;
                        }  

                        Move move = new Move(bufferStr.Substring(1,6));

                        // illegal move
                        if (   (bufferStr.StartsWith("+") && threadPlayer.MyColor() != Color.BLACK)
                            || (bufferStr.StartsWith("-") && threadPlayer.MyColor() != Color.WHITE)
                            || !games[gameIdx].pos.IsLegalMove(move))
                        {
                            // DEBUG
                            games[gameIdx].pos.PrintPosition();

                            WriteStream(threadPlayer.Stream(), "#ILLEGAL\n");
                            WriteStream(threadPlayer.Stream(), "#LOSE\n");
                            //tell opponent game ended
                            WriteStream(threadPlayer.Opponent().Stream(), "#GAME_OVER\n");
                            WriteStream(threadPlayer.Opponent().Stream(), "#WIN\n");
                            break;
                        }

                        // OKを送る
                        string mStr = (move.Promote() ? bufferStr.Substring(0, 6) : bufferStr.Substring(0, 5)) + ",OK\n";
                        WriteStream(threadPlayer.Stream(), mStr);
                        WriteStream(threadPlayer.Opponent().Stream(), mStr);
                        
                        // do move
                        if (games[gameIdx].pos.DoMove(move)) {
                            WriteStream(threadPlayer.Stream(), "#GAME_OVER\n");
                            WriteStream(threadPlayer.Stream(), "#WIN\n");
                            //tell opponent game ended
                            WriteStream(threadPlayer.Opponent().Stream(), "#GAME_OVER\n");
                            WriteStream(threadPlayer.Opponent().Stream(), "#LOSE\n");
                            break;
                        }
                    }
                    // chat
                    else
                    {
                        threadPlayer.Opponent().Stream().Write(buffer, 0, bytesRead);
                    }
                }
                // Client closed
                catch (System.IO.IOException)
                {
                    break;
                }
            }
            Console.WriteLine("Player #" + threadPlayer.PlayerId() + " left");
            if (threadPlayer.Opponent() != null)
            {
                threadPlayer.Opponent().Stream().Close();
                threadPlayer.Opponent().Tcp().Close();
                players.Remove(threadPlayer.Opponent());
            }
            threadStream.Close();
            threadClient.Close();
            players.Remove(threadPlayer);

            lock(waitingPlayerLock)
            {
                if (waitingPlayer != null)
                {
                    if (waitingPlayer.PlayerId() == threadPlayer.PlayerId())
                    {
                        waitingPlayer = null;
                    }
                }
            }
            
            // find game from gameID and see if can remove from list
            lock (gamesLock)
            {
                for (int i = 0; i < games.Count; i++)
                {
                    if (games[i].Id() == threadPlayer.GameId())
                    {
                        if (threadPlayer.Opponent() != null)
                        {
                            if (games[i].Player1().Tcp().Connected == false && games[i].Player2().Tcp().Connected == false)
                            {
                                games.RemoveAt(i);
                                break;
                            }
                        }
                    }
                }
            }
        }

        // 暫定的に作成
        // todo : 修正
        private async void MakeHtml()
        {
            while (true)
            {
                string htmlData;

                htmlData = "<!DOCTYPE html>\n";
                htmlData += "<html>\n";
                htmlData += "<head> <meta charset=\"utf-8\"/> </head>\n";
                htmlData += "<body> 接続台数 : " + players.Count + "</body>\n";
                htmlData += "</html>";

                File.WriteAllText(@"./web/index.html", htmlData);

                await Task.Delay(10000);
            }
        }
    }
}