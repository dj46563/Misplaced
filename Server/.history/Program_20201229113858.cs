﻿using System;
using Mirror.SimpleWeb;
using System.Security.Authentication;
using System.Collections.Generic;
using NetStack.Serialization;
using NetStack.Quantization;
using System.Timers;
using System.Linq;

namespace Server
{
    public class PlayerData {
        public ushort id;
        public string name;
        public bool isNew;
        public uint qX;
        public uint qY;
        public short points;
        public List<ushort> guesses;

        // Default ctor
        public PlayerData() {
            id = ushort.MaxValue;
            name = "null";
            isNew = true;
            qX = uint.MaxValue;
            qY = uint.MaxValue;
            points = 0;
            guesses = new List<ushort>();
        }

        // Copy ctor
        public PlayerData(PlayerData copy) {
            id = copy.id;
            name = copy.name;
            qX = copy.qX;
            qY = copy.qY;
            points = copy.points;
            guesses = copy.guesses;
        }
    }
    class Program
    {
        public static readonly float SECONDS_WAITING_IN_BEGIN = 3f;
        public static readonly float SECONDS_WAITING_IN_BUILD = 15f;
        public static readonly float SECONDS_WAITING_IN_SEARCH = 30f;
        public static readonly float SECONDS_WAITING_IN_SCORING = 3f;
        public static readonly int NUMBER_OF_MOVEABLE_OBJECTS = 3;

        private static Timer beginTimer, buildTimer, searchTimer, scoringTimer;

        private static SimpleWebServer _webServer;
        private static List<int> _connectedIds = new List<int>();
        private static ushort _handshakenClientCount = 0;
        private static Dictionary<int, PlayerData> _playerDatas = new Dictionary<int, PlayerData>();
        private static Queue<PlayerData> _dataToSend = new Queue<PlayerData>();

        private static BitBuffer _bitBuffer = new BitBuffer(1024);
        private static byte[] _buffer = new byte[2048];

        enum GameState {Waiting = 0, Begin, Builder, Search, Scoring}
        private static GameState _currentState = GameState.Waiting;
        private static Dictionary<ushort, Tuple<ushort, ushort>> _movedObjects;
        private static bool _waitingOnStateTimer = false;
        private static int _builderId;

        private static Random _rand;

        static void Main(string[] args)
        {
            _rand = new Random(Environment.TickCount);

            SslConfig sslConfig;
            TcpConfig tcpConfig = new TcpConfig(true, 5000, 20000);
            Console.WriteLine("Setting up secure server");
            sslConfig = new SslConfig(true, "cert.pfx", "", SslProtocols.Tls12);
            _webServer = new SimpleWebServer(10000, tcpConfig, 16*1024, 3000, sslConfig);
            _webServer.Start(Constants.GAME_PORT);
            Console.WriteLine("Server started");

            _webServer.onConnect += WebServerOnConnect;
            _webServer.onData += WebServerOnData;
            _webServer.onDisconnect += WebServerOnDisconnect;

            Timer stateUpdateTimer = new Timer(1f / Constants.SERVER_TICKRATE * 1000);
            stateUpdateTimer.Elapsed += StateUpdateTimerOnElapsed;
            stateUpdateTimer.AutoReset = true;
            stateUpdateTimer.Enabled = true;

            while (!Console.KeyAvailable) {
                _webServer.ProcessMessageQueue();

                // GUARD, DONT DO STATE STUFF IF WE ARE WAITING
                if (_waitingOnStateTimer) continue;
                switch(_currentState) {
                    case GameState.Waiting:
                    {
                        if (_handshakenClientCount >= 2) {
                            _currentState = GameState.Begin;
                            SendStateUpdate(_currentState);
                        }

                        break;
                    }
                    case GameState.Begin:
                    {
                        // Set timer to go to builder state
                        beginTimer = new Timer(SECONDS_WAITING_IN_BEGIN * 1000);
                        beginTimer.AutoReset = false;
                        beginTimer.Start();
                        _waitingOnStateTimer = true;

                        beginTimer.Elapsed += delegate(Object source, ElapsedEventArgs e) {
                            _waitingOnStateTimer = false;

                            _movedObjects = new Dictionary<ushort, Tuple<ushort, ushort>>();
                            _currentState = GameState.Builder;
                            SendStateUpdate(_currentState);
                        };
                        break;
                    }
                    case GameState.Builder:
                    {
                        // Set timer to go to builder state
                        buildTimer = new Timer(SECONDS_WAITING_IN_BUILD * 1000);
                        buildTimer.AutoReset = false;
                        buildTimer.Start();
                        _waitingOnStateTimer = true;

                        buildTimer.Elapsed += delegate(Object source, ElapsedEventArgs e) {
                            _waitingOnStateTimer = false;

                            // Reset everyones guesses
                            foreach (PlayerData playerData in _playerDatas.Values) {
                                playerData.guesses.Clear();
                            }
                            _currentState = GameState.Search;
                            SendStateUpdate(_currentState);
                        };
                        break;
                    }
                    case GameState.Search:
                    {
                        // Set timer to go to scoring state
                        searchTimer = new Timer(SECONDS_WAITING_IN_SEARCH * 1000);
                        searchTimer.AutoReset = false;
                        searchTimer.Start();
                        _waitingOnStateTimer = true;

                        searchTimer.Elapsed += delegate(Object source, ElapsedEventArgs e) {
                            _waitingOnStateTimer = false;

                            _currentState = GameState.Scoring;
                            SendStateUpdate(_currentState);
                        };
                        break;
                    }
                    case GameState.Scoring:
                    {
                        // Set timer to wait for points to come in from clients
                        scoringTimer = new Timer(SECONDS_WAITING_IN_SCORING * 1000);
                        scoringTimer.AutoReset = false;
                        scoringTimer.Start();
                        _waitingOnStateTimer = true;

                        scoringTimer.Elapsed += delegate(Object source, ElapsedEventArgs e) {
                            _waitingOnStateTimer = false;

                            // Tell everyone everyones scores
                            _bitBuffer.Clear();
                            _bitBuffer.AddByte(7);
                            _bitBuffer.AddUShort((ushort)_playerDatas.Count);

                            foreach (PlayerData data in _playerDatas.Values) {
                                _bitBuffer.AddUShort(data.id);
                                _bitBuffer.AddShort(data.points);
                                
                                Console.WriteLine($"Points: {data.id} {data.points}");
                            }

                            _bitBuffer.ToArray(_buffer);
                            _webServer.SendAll(_connectedIds, new ArraySegment<byte>(_buffer, 0, 3 + 4 * _playerDatas.Count));

                            if (_handshakenClientCount >= 2) {
                                _currentState = GameState.Begin;
                            }
                            else {
                                _currentState = GameState.Waiting;
                            }
                            SendStateUpdate(_currentState);
                        };

                        break;
                    }
                }
            }

            Console.WriteLine("Closing server");
            _webServer.Stop();
        }

        static void WebServerOnConnect(int id) {
            _connectedIds.Add(id);
            _playerDatas[id] = new PlayerData() {
                id = (ushort)id
            };

            // Tell new client their id and the game state and everyone's name
            _bitBuffer.Clear();
            _bitBuffer.AddByte(2);
            _bitBuffer.AddUShort((ushort)id);
            _bitBuffer.AddByte((byte)_currentState);

            _bitBuffer.AddUShort((ushort)_playerDatas.Count);
            foreach (var playerData in _playerDatas.Values)
            {
                _bitBuffer.AddUShort(playerData.id);
                _bitBuffer.AddString(playerData.name);
            }

            _bitBuffer.ToArray(_buffer);
            _webServer.SendOne(id, new ArraySegment<byte>(_buffer, 0, 5 + _playerDatas.Count * 20));
        }

        static void WebServerOnData(int id, ArraySegment<byte> data) {
            _bitBuffer.Clear();
            _bitBuffer.FromArray(data.Array, data.Count);

            byte messageId = _bitBuffer.ReadByte();
            switch (messageId) {
                case 1:
                {
                    uint qX = _bitBuffer.ReadUInt();
                    uint qY = _bitBuffer.ReadUInt();

                    PlayerData playerData = _playerDatas[id];
                    playerData.qX = qX;
                    playerData.qY = qY;

                    // Send this position to everyone next state packet
                    _dataToSend.Enqueue(playerData);
                    
                    break;
                }
                case 8:
                {
                    string name = _bitBuffer.ReadString();
                    _playerDatas[id].name = name;

                    _handshakenClientCount += 1;

                    // Tell all the players this new client's name
                    _bitBuffer.Clear();
                    _bitBuffer.AddByte(9);
                    _bitBuffer.AddUShort(_playerDatas[id].id);
                    _bitBuffer.AddString(_playerDatas[id].name);
                    _bitBuffer.ToArray(_buffer);
                    _webServer.SendAll(_connectedIds, new ArraySegment<byte>(_buffer, 0, 23));

                    break;
                }
                case 10:
                {
                    ushort objectId = _bitBuffer.ReadUShort();
                    ushort newX = _bitBuffer.ReadUShort();
                    ushort newY = _bitBuffer.ReadUShort();
                    _movedObjects[objectId] = new Tuple<ushort, ushort>(newX, newY);

                    break;
                }
                case 11:
                {
                    short pointChange = _bitBuffer.ReadShort();
                    _playerDatas[id].points += pointChange;

                    // If points are 0 or less, give builder a point
                    if (pointChange <= 0) {
                        _playerDatas[_builderId].points += 1;
                    }

                    break;
                }
            }
        }

        static void WebServerOnDisconnect(int id) {
            _connectedIds.Remove(id);
            _playerDatas.Remove(id);

            _handshakenClientCount -= 1;

            // Tell other players about the disconnection
            _bitBuffer.Clear();
            _bitBuffer.AddByte(4);
            _bitBuffer.AddUShort((ushort)id);
            _bitBuffer.ToArray(_buffer);
            _webServer.SendAll(_connectedIds, new ArraySegment<byte>(_buffer, 0, 3));

            // Check if we have less than 2 players and should cancel the game
            if (_currentState != GameState.Waiting && _handshakenClientCount < 2) {
                beginTimer?.Stop();
                buildTimer?.Stop();
                searchTimer?.Stop();
                scoringTimer?.Stop();
                _currentState = GameState.Waiting;
                SendStateUpdate(_currentState);
            }
        }

        private static void StateUpdateTimerOnElapsed(Object source, ElapsedEventArgs e) {
            _bitBuffer.Clear();
            _bitBuffer.AddByte(3);
            _bitBuffer.AddUShort((ushort)_dataToSend.Count);
            foreach (PlayerData playerData in _dataToSend) {
                _bitBuffer.AddUShort(playerData.id);
                _bitBuffer.AddUInt(playerData.qX);
                _bitBuffer.AddUInt(playerData.qY);
            }

            _bitBuffer.ToArray(_buffer);
            _webServer.SendAll(_connectedIds, new ArraySegment<byte>(_buffer, 0, 3 + 10 * _dataToSend.Count));

            _dataToSend.Clear();
        }

        private static void SendStateUpdate(GameState currentState) {
            Console.WriteLine("Changing state to: " + currentState.ToString());
            _waitingOnStateTimer = false;

            _bitBuffer.Clear();
            _bitBuffer.AddByte(5);
            _bitBuffer.AddByte((byte)currentState);

            // Chose a random builder and tell everyone
            if (currentState == GameState.Begin) {
                int randomIndex = _rand.Next(0, _connectedIds.Count);
                _builderId = _connectedIds[randomIndex];
                _bitBuffer.AddUShort((ushort)_builderId);

                _bitBuffer.ToArray(_buffer);
                _webServer.SendAll(_connectedIds, new ArraySegment<byte>(_buffer, 0, 6));
            }
            // Tell everyone the moved items
            else if (currentState == GameState.Search) {
                _bitBuffer.AddUShort((ushort)_movedObjects.Count);
                foreach (var movedObject in _movedObjects) {
                    _bitBuffer.AddUShort(movedObject.Key);
                    _bitBuffer.AddUShort(movedObject.Value.Item1);
                    _bitBuffer.AddUShort(movedObject.Value.Item2);
                }

                _bitBuffer.ToArray(_buffer);
                _webServer.SendAll(_connectedIds, new ArraySegment<byte>(_buffer, 0, 6 + 12 * Constants.MAX_SHIFTED_OBJECTS));
            }
            else {
                _bitBuffer.ToArray(_buffer);
                _webServer.SendAll(_connectedIds, new ArraySegment<byte>(_buffer, 0, 2));
            }
            
        }
    }
}
