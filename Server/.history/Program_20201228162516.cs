﻿using System;
using Mirror.SimpleWeb;
using System.Security.Authentication;
using System.Collections.Generic;
using NetStack.Serialization;
using NetStack.Quantization;
using System.Timers;

namespace Server
{
    public class PlayerData {
        public int id;
        public bool isNew;
        public uint qX;
        public uint qY;
        public ushort points;
    }
    class Program
    {
        private static SimpleWebServer _webServer;
        private static List<int> _connectedIds = new List<int>();
        private static Dictionary<int, PlayerData> _playerDatas = new Dictionary<int, PlayerData>();
        private static Queue<PlayerData> _dataToSend = new Queue<PlayerData>();
        private static BitBuffer _bitBuffer = new BitBuffer(1024);

        enum GameState {Waiting, Begin, Builder, Search, Scoring}
        static GameState _currentState = GameState.Waiting;

        static void Main(string[] args)
        {
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
            }

            Console.WriteLine("Closing server");
            _webServer.Stop();
        }

        static void WebServerOnConnect(int id) {
            _connectedIds.Add(id);
            _playerDatas[id] = new PlayerData() {
                id = id,
                isNew = true,
                points = 0,
                qX = 0,
                qY = 0
            };
        }

        static void WebServerOnData(int id, ArraySegment<byte> data) {
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
            }
        }

        static void WebServerOnDisconnect(int id) {
            _connectedIds.Remove(id);
            _playerDatas.Remove(id);
        }

        private static void StateUpdateTimerOnElapsed(Object source, ElapsedEventArgs e) {
            _bitBuffer.Clear();
            _bitBuffer.AddByte(3);
        }
    }
}
