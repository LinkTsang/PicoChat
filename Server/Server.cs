﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using PicoChat.Common;
using System.Linq;
using System.Text;

namespace PicoChat
{
    public class Room
    {
        string name;
        public string Name => name;

        public Room(string name)
        {
            this.name = name;
        }

        readonly LinkedList<Connection> connections = new LinkedList<Connection>();
        public bool Add(Connection connection)
        {
            if (!connections.Contains(connection))
            {
                connections.AddFirst(connection);
                return true;
            }
            return false;
        }

        public bool Remove(Connection connection)
        {
            if (connections.Contains(connection))
            {
                connections.Remove(connection);
                return true;
            }
            return false;
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public void SendMessage(Message message)
        {
            foreach (var connection in connections)
            {
                if (!connection.ClientName.Equals(message.author))
                {
                    connection.SendMessage(message);
                }
            }
        }
    }

    public sealed class Server
    {
        readonly Dictionary<string, Room> rooms = new Dictionary<string, Room>();
        readonly Dictionary<string, Connection> connections = new Dictionary<string, Connection>();
        //ConcurrentBag<Connection> connections = new ConcurrentBag<Connection>();
        CancellationTokenSource cts = new CancellationTokenSource();
        long clientCount_;

        public IPAddress Address { get; }
        public int Port { get; }
        long ConnectionCount => clientCount_;
        public Dictionary<string, Room> Rooms => rooms;

        public Server(IPAddress address, int port)
        {
            Address = address;
            Port = port;
        }

        public void Start()
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(Address, Port));
            socket.Listen(backlog: 1024);

            Console.WriteLine($"Listening on {Address}:{Port}...");

            var tf = new TaskFactory(TaskCreationOptions.LongRunning, TaskContinuationOptions.None);
            tf.StartNew(() =>
            {
                Console.WriteLine("Listener task started");
                while (true)
                {
                    if (cts.Token.IsCancellationRequested)
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        break;
                    }
                    Console.WriteLine("Waiting for accept...");
                    Socket client = socket.Accept();
                    if (!client.Connected)
                    {
                        Console.WriteLine("Not connected");
                        continue;
                    }
                    IPEndPoint remoteEndPoint = (IPEndPoint)client.RemoteEndPoint;
                    Console.WriteLine($"Client {remoteEndPoint.Address}:{remoteEndPoint.Port} Connected; " +
                        $"Current Connection: {Interlocked.Increment(ref clientCount_)}");

                    Task task = CommunicateWithClientUsingSocketAsync(client);
                    task.GetAwaiter().OnCompleted(() =>
                    {
                        Console.WriteLine($"Client {remoteEndPoint.Address}:{remoteEndPoint.Port} Connected; " +
                            $"Current Connection: {Interlocked.Decrement(ref clientCount_)}");
                    });
                }

                socket.Dispose();
                Console.WriteLine("The sever is closed.");
            }, cts.Token);
        }

        public void Stop()
        {
            cts.Cancel();
        }

        Task CommunicateWithClientUsingSocketAsync(Socket socket)
        {
            return Task.Run(() =>
            {
                Connection connection = new Connection(socket);
                IPEndPoint remoteEndPoint = (IPEndPoint)socket.RemoteEndPoint;
                connection.Login += (sender, name) =>
                {
                    Connection @this = sender as Connection;
                    if (@this == null) return;
                    if (@this.ClientName != null)
                    {
                        @this.SendMessage(MessageType.SYSTEM_LOGIN_FAILED, $"Already logged as \"{@this.ClientName}\"");
                    }
                    else
                    {
                        lock (connections)
                        {
                            if (!connections.ContainsKey(name))
                            {
                                connections.Add(name, @this);
                                @this.ClientName = name;
                                @this.SendMessage(MessageType.SYSTEM_LOGIN_OK);
                                Console.WriteLine($"Client {remoteEndPoint.Address}:{remoteEndPoint.Port} logged in as \"{@this.ClientName}\".");
                            }
                            else
                            {
                                @this.SendMessage(MessageType.SYSTEM_LOGIN_FAILED, $"The name \"{name}\" is already taken");
                            }
                        }
                    }
                };
                connection.Logout += (sender, name) =>
                {
                    Connection @this = sender as Connection;
                    if (@this == null) return;
                    if (@this.ClientName == null)
                    {
                        @this.SendMessage(MessageType.NO_LOGGED);
                        return;
                    }
                    lock (connections)
                    {
                        if (connections.ContainsKey(@this.ClientName))
                        {
                            lock (@this.JoinedRooms)
                            {
                                @this.JoinedRooms.ForEach(rname =>
                                {
                                    if (Rooms.TryGetValue(rname, out Room room))
                                    {
                                        room.Remove(@this);
                                    }
                                });
                            }
                            @this.SendMessage(MessageType.SYSTEM_OK, $"Logged out");
                            @this.ClientName = null;
                            Console.WriteLine($"\"{@this.ClientName}\" [{remoteEndPoint.Address}:{remoteEndPoint.Port}] logged out.");
                        }
                        else
                        {
                            @this.SendMessage(MessageType.NO_LOGGED);
                        }
                    }

                };
                connection.JoinRoom += (sender, roomName) =>
                {
                    Connection @this = sender as Connection;
                    if (@this == null) return;
                    if (@this.ClientName == null)
                    {
                        @this.SendMessage(MessageType.NO_LOGGED);
                        return;
                    }
                    lock (Rooms)
                    {
                        if (!Rooms.ContainsKey(roomName))
                        {
                            Rooms.Add(roomName, new Room(roomName));
                            Console.WriteLine($"Room[roomName] created.");
                        }
                        Room room = Rooms[roomName];
                        if (room.Add(@this))
                        {
                            lock (@this.JoinedRooms) @this.JoinedRooms.Add(room.Name);
                            IPEndPoint endPoint = (IPEndPoint)@this.Socket.RemoteEndPoint;
                            Console.WriteLine($"{@this.ClientName} [{endPoint.Address}:{endPoint.Port}] joined the room[{room.Name}]");
                            @this.SendMessage(MessageType.SYSTEM_OK, $"Joined the room[{room.Name}]");
                            Console.WriteLine($"{@this.ClientName} [{remoteEndPoint.Address}:{remoteEndPoint.Port}] joined the room[{room.Name}].");
                        }
                        else
                        {
                            @this.SendMessage(MessageType.ALREADY_JOINNED, $"Already joined the room[{room.Name}]");
                        }
                    }
                };

                connection.LeaveRoom += (sender, roomName) =>
                {
                    Connection @this = sender as Connection;
                    if (@this == null) return;
                    if (@this.ClientName == null)
                    {
                        @this.SendMessage(MessageType.SYSTEM_ERROR);
                        return;
                    }

                    lock (Rooms)
                    {
                        if (Rooms.ContainsKey(roomName))
                        {
                            Room room = Rooms[roomName];
                            room.Remove(@this);
                            lock (@this.JoinedRooms) @this.JoinedRooms.Remove(room.Name);

                            IPEndPoint endPoint = (IPEndPoint)@this.Socket.RemoteEndPoint;
                            Console.WriteLine($"{@this.ClientName} [{endPoint.Address}:{endPoint.Port}] left the room [{room.Name}]");
                            @this.SendMessage(MessageType.SYSTEM_OK, $"Left from the room[{roomName}]");
                            Console.WriteLine($"\"{@this.ClientName}\" [{remoteEndPoint.Address}:{remoteEndPoint.Port}] left the room[{room.Name}].");
                        }
                        else
                        {
                            @this.SendMessage(MessageType.SYSTEM_ERROR, $"No joinned the room[{roomName}]");
                        }
                    }
                };

                connection.ListRooms += (sender, e) =>
                {
                    Connection @this = sender as Connection;
                    if (@this == null) return;
                    if (@this.ClientName == null)
                    {
                        @this.SendMessage(MessageType.NO_LOGGED);
                        return;
                    }
                    string s = null;
                    lock (@this.JoinedRooms) s = Serializer.Serialize(@this.JoinedRooms);
                    @this.SendMessage(MessageType.CLIENT_LIST_JOINED_ROOMS, s);
                };

                connection.MessageReceived += (sender, message) =>
                {
                    Connection @this = sender as Connection;
                    if (@this == null) return;
                    if (message == null)
                    {
                        @this.SendMessage(MessageType.SYSTEM_ERROR);
                    }
                    else if (@this.ClientName == null || !@this.ClientName.Equals(message.author))
                    {
                        @this.SendMessage(MessageType.NO_LOGGED);
                    }
                    else if (message.room == null)
                    {
                        @this.SendMessage(MessageType.SYSTEM_UNJOIN_ROOM);
                    }
                    else if (Rooms.TryGetValue(message.room, out Room room))
                    {
                        room.SendMessage(message);
                    }
                    else
                    {
                        @this.SendMessage(MessageType.SYSTEM_UNJOIN_ROOM);
                    }
                };

                connection.Closing += (sender, e) =>
                {
                    Connection @this = sender as Connection;
                    if (@this == null) return;
                    lock (Rooms)
                    {
                        foreach (string name in @this.JoinedRooms)
                        {
                            if (Rooms.ContainsKey(name))
                            {
                                Room room = Rooms[name];
                                room.Remove(@this);
                            }
                        }
                    }
                    IPEndPoint endPoint = (IPEndPoint)@this.Socket.RemoteEndPoint;
                    Debug.WriteLine($"Client [{endPoint.Address}:{endPoint.Port}] closed");
                };

                connection.Closed += (sender, e) =>
                {
                    Connection @this = sender as Connection;
                    if (@this == null) return;
                };
                connection.Handle();
            });
        }

    }
}
