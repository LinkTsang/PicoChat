﻿using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PicoChat.Common;
using PicoChat.Models;
using PicoChat.Servers;

namespace PicoChat.ViewModels
{
    public class ChatViewModel : BindableBase
    {
        private const string AppName = "PicoChat";
        private static Dispatcher Dispatcher => Application.Current.Dispatcher;
        private readonly IClient _client;
        private readonly IWindowServer _windowServer;
        private readonly ObservableCollection<ChatMessage> _messagesWaitToConfirm = new ObservableCollection<ChatMessage>();
        private readonly ObservableCollection<ChatFileMessage> _transferingFiles = new ObservableCollection<ChatFileMessage>();
        public ObservableCollection<ChatMessage> Messages { get; } = new ObservableCollection<ChatMessage>();
        public ObservableCollection<string> JoinRooms { get; } = new ObservableCollection<string>();

        private MessageColorInfo _messageColorInfo = new MessageColorInfo("#FF000000");
        public MessageColorInfo MessageColorInfo
        {
            get => _messageColorInfo;
            set => SetProperty(ref _messageColorInfo, value);
        }


        private MessageFontInfo _messageFontInfo = new MessageFontInfo("Consolas", 14);
        public MessageFontInfo MessageFontInfo
        {
            get => _messageFontInfo;
            set => SetProperty(ref _messageFontInfo, value);
        }

        private string _title;
        public string Title
        {
            get => _title;
            set => SetProperty(ref _title, value);
        }

        public bool Connected => _client.Connected;

        private string _selectedRoom;
        public string SelectedRoom
        {
            get => _selectedRoom;
            set => SetProperty(ref _selectedRoom, value);
        }

        private string _messageToSend = "";
        public string MessageToSend
        {
            get => _messageToSend;
            set => SetProperty(ref _messageToSend, value);
        }

        public ICommand SendMessageCommand { get; }
        public ICommand SendImageCommand { get; }
        public ICommand SendFileCommand { get; }
        public ICommand PullFileCommand { get; }

        public ICommand SetMessageColorCommand { get; }
        public ICommand SetMessageFontCommand { get; }

        public ICommand JoinRoomCommand { get; }
        public ICommand LeaveRoomCommand { get; }

        public ICommand DisconnectCommand { get; }
        public ICommand LogoutCommand { get; }

        public ChatViewModel(IWindowServer windowServer, IClient client)
        {
            _windowServer = windowServer;
            _client = client;

            SendMessageCommand = new DelegateCommand(OnSendMessage);
            SendImageCommand = new DelegateCommand(OnSendImage);
            SendFileCommand = new DelegateCommand<string>(OnSendFile);
            PullFileCommand = new DelegateCommand<ChatFileMessage>(OnPullFile);
            JoinRoomCommand = new DelegateCommand<string>(OnJoinRoom);

            SetMessageColorCommand = new DelegateCommand(OnSetMessageColorInfo);
            SetMessageFontCommand = new DelegateCommand(OnSetMessageFontInfo);

            _client.UnknownMessageReceived += Client_UnknownMessageReceived;
            _client.LoginOK += Client_LoginOK;
            _client.LoginFailed += Client_LoginFailed;
            _client.LogoutOK += Client_LogoutOK;
            _client.JoinedInRoom += Client_JoinedInRoom;
            _client.LeavedFromRoom += Client_LeavedFromRoom;
            _client.MessageReceived += Client_MessageReceived;
            _client.ImageMessageReceived += Client_ImageMessageReceived;
            _client.MessageArrivied += Client_MessageArrivied;
            _client.StateChaged += Client_StateChaged;
            _client.SocketExceptionRaising += Client_SocketExceptionRaising;
            _client.ReceiverTaskExited += (_, __) => { _client.Disconnect(); };
            _client.SystemMessageReceived += Client_OnSystemMessageReceived;
            _client.FileMessageReived += Client_OnFileMessageReived;
            _client.FileReived += Client_FileReived;

            Messages.CollectionChanged += (sender, e) =>
            {
                Title = $"{AppName} - {_client.Name} - [{_client.CurrentRoomName}] - {Messages.Count}";
            };

            PropertyChanged += (sender, args) =>
            {
                if (args.PropertyName == nameof(SelectedRoom))
                {
                    _client.CurrentRoomName = SelectedRoom;
                    Title = $"{AppName} - {_client.Name} - [{_client.CurrentRoomName}] - {Messages.Count}";
                }
            };
            FireInfo(_client.Name != null ? $"Hello {_client.Name}~" : "Hello, please login...");
            FireInfo("Use /? for help.");
        }

        private void OnJoinRoom(string roomName)
        {
            _client.Join(roomName);
        }

        private void OnSetMessageFontInfo(object obj)
        {
            var fontInfo = _windowServer.GetFontInfo();
            if (fontInfo != null) MessageFontInfo = fontInfo;
        }

        private void OnSetMessageColorInfo(object obj)
        {
            var colorInfo = _windowServer.GetColorInfo();
            if (colorInfo != null) MessageColorInfo = colorInfo;
        }

        private void OnPullFile(ChatFileMessage chatFileMessage)
        {
            if (Connected)
            {
                chatFileMessage.IsTransfering = true;
                chatFileMessage.Process = 10;

                _transferingFiles.Add(chatFileMessage);
                _client.PullFile(chatFileMessage.FileId);
            }
        }

        private void OnSendFile(string filename)
        {
            SendFile(filename);
        }

        public void SendFile(string filename)
        {
            var fileInfo = new FileInfo(filename);
            if (Connected)
            {
                var id = Utility.GenerateID();
                var fileId = Utility.GenerateID();

                var fileMessage = new FileMessage(id, SelectedRoom, _client.Name, fileId, fileInfo.Name, fileInfo.Length);
                var chatFileMessage = new ChatFileMessage(fileMessage)
                {
                    Name = "<this>",
                    IsLocalMessage = true
                };
                Messages.Add(chatFileMessage);
                _messagesWaitToConfirm.Add(chatFileMessage);

                fileMessage.data = File.ReadAllBytes(fileInfo.FullName);
                _client.PushFile(fileMessage);
            }
            else
            {
                FireError("FAILED TO SEND IMAGE MESSAGE, PLEASE CHECK YOUR CONNECTION");
                FireInfo("PLEASE USE /connect TO CONNECT");
            }
        }

        private void OnSendImage(object parameter)
        {
            SendImage(parameter as string);
        }

        public void SendImage(string fileName)
        {
            if (Connected)
            {
                var id = Utility.GenerateID();
                using (var bitmap = Image.FromFile(fileName) as Bitmap)
                using (var memory = new MemoryStream())
                {
                    if (bitmap == null) return;
                    bitmap.Save(memory, ImageFormat.Png);
                    memory.Position = 0;
                    BitmapImage bitmapImage = new BitmapImage();
                    bitmapImage.BeginInit();
                    bitmapImage.StreamSource = memory;
                    bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                    bitmapImage.EndInit();

                    ChatMessage message = new ChatImageMessage(id, "<this>", _client.CurrentRoomName, bitmapImage);
                    Messages.Add(message);
                    _messagesWaitToConfirm.Add(message);
                    _client.SendMessage(id, _client.CurrentRoomName, bitmap);
                }
            }
            else
            {
                FireError("FAILED TO SEND IMAGE MESSAGE, PLEASE CHECK YOUR CONNECTION");
                FireInfo("PLEASE USE /connect TO CONNECT");
            }
        }


        private void OnSendMessage(object parameter)
        {
            if (!MessageToSend.StartsWith("/", StringComparison.Ordinal))
            {
                if (_client.Connected)
                {
                    var id = Utility.GenerateID();
                    var chatTextMessage = new ChatTextMessage(id, "<this>", _client.CurrentRoomName, MessageToSend)
                    {
                        FontInfo = _messageFontInfo,
                        ColorInfo = _messageColorInfo
                    };
                    Messages.Add(chatTextMessage);
                    _messagesWaitToConfirm.Add(chatTextMessage);

                    var message = new Message(id, _client.Name, _client.CurrentRoomName, MessageToSend)
                    {
                        ColorInfo = _messageColorInfo,
                        FontInfo = _messageFontInfo
                    };
                    _client.SendMessage(message);
                }
                else
                {
                    FireError("FAILED TO SEND MESSAGE, PLEASE CHECK YOUR CONNECTION");
                    FireInfo("PLEASE USE /connect TO CONNECT");
                }
            }
            else
            {
                string[] argv = MessageToSend.Split(' ');
                string command = argv[0];
                switch (command)
                {
                    case "/?":
                        _windowServer.ShowHelpDialog();
                        break;
                    case "/connect":
                        if (!_client.Connected)
                        {
                            if (argv.Length != 3)
                            {
                                FireError($"Invalid Argument {MessageToSend}");
                                break;
                            }
                            if (!IPAddress.TryParse(argv[1], out IPAddress address))
                            {
                                FireError($"Invalid IP address {address}");
                                break;
                            }
                            _client.ServerAddress = address;
                            if (!int.TryParse(argv[2], out int port))
                            {
                                FireError($"Invalid port {port}");
                                break;
                            }
                            _client.ServerPort = port;
                            FireInfo($"CONNECTING to {_client.ServerAddress}:{_client.ServerPort}...");
                            _client.Connect();
                            var _ = _client.HandleAsync();
                        }
                        else
                        {
                            FireError($"The client has connected to {_client.ServerAddress}:{_client.ServerPort}.");
                        }
                        break;
                    case "/disconnect":
                        if (_client.Connected)
                        {
                            _client.Disconnect();
                            FireInfo("Disconnecting...");
                        }
                        else
                        {
                            FireError("The client isn't connected.");
                        }
                        break;
                    case "/login":
                        if (argv.Length != 2)
                        {
                            FireError($"Invalid Command {MessageToSend}");
                            break;
                        }
                        _client.Login(argv[1]);
                        break;
                    case "/logout":
                        _client.Logout();
                        break;
                    case "/join":
                        if (argv.Length != 2)
                        {
                            FireError($"Invalid Command {MessageToSend}");
                            break;
                        }
                        _client.Join(argv[1]);
                        break;
                    case "/leave":
                        if (argv.Length != 2)
                        {
                            FireError($"Invalid Command {MessageToSend}");
                            break;
                        }
                        _client.CurrentRoomName = "";
                        _client.Leave(argv[1]);
                        break;
                    case "/list":
                        _client.ListJoinedRooms();
                        break;
                    default:
                        FireError($"Invalid Command {MessageToSend}");
                        break;
                }
            }
            MessageToSend = "";
        }

        private void FireInfo(string message)
        {
            Messages.Add(new ChatSystemMessage("[Info]", message));
        }

        private void FireInfo(string tag, string message)
        {
            Messages.Add(new ChatSystemMessage(tag, message));
        }

        private void FireError(string message)
        {
            Messages.Add(new ChatSystemMessage("[Error]", message));
        }

        private void Client_LoginOK(object sender, LoginInfo e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Title = $"{AppName} - {_client.Name} - [{_client.CurrentRoomName}] - {Messages.Count}";
                FireInfo(e.Content);
            }));
        }

        private void Client_LoginFailed(object sender, LoginInfo e)
        {
            Dispatcher.BeginInvoke(new Action(() => FireError(e.Content)));
        }

        private void Client_LogoutOK(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Title = $"{AppName} - - [{_client.CurrentRoomName}] - {Messages.Count}";
            }));
        }

        private void Client_JoinedInRoom(object sender, RoomInfo e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                JoinRooms.Add(e.Name);
                SelectedRoom = e.Name;
            }));
        }

        private void Client_LeavedFromRoom(object sender, RoomInfo e)
        {
            Dispatcher.BeginInvoke(new Action(() => JoinRooms.Remove(e.Name)));
        }

        private void Client_OnSystemMessageReceived(object o, string content)
        {
            Dispatcher.BeginInvoke(new Action(() => FireInfo("[SYS]", content)));
        }

        private void Client_UnknownMessageReceived(object sender, Client.SystemMessageEventArgs e)
        {
            string content = e.Data != null ? Encoding.UTF8.GetString(e.Data) : "";
            Dispatcher.BeginInvoke(new Action(() => FireInfo($"[{e.Type}]", content)));
        }

        private void Client_SocketExceptionRaising(object sender, SocketException e)
        {
            Dispatcher.BeginInvoke(new Action(() => FireInfo("[SocketException]", e.Message)));
        }

        private void Client_MessageReceived(object sender, Message e)
        {
            Dispatcher.BeginInvoke(new Action(() => Messages.Add(new ChatTextMessage(e))));
        }

        private void Client_ImageMessageReceived(object sender, ImageMessage e)
        {
            Dispatcher.BeginInvoke(new Action(() => Messages.Add(new ChatImageMessage(e))));
        }

        private void Client_MessageArrivied(object o, Receipt receipt)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                for (int i = 0; i < _messagesWaitToConfirm.Count; ++i)
                {
                    if (_messagesWaitToConfirm[i].ID == receipt.ID)
                    {
                        _messagesWaitToConfirm[i].HasReceipt = true;
                        _messagesWaitToConfirm.RemoveAt(i);
                    }
                }
                CollectionViewSource.GetDefaultView(Messages).Refresh();
            }));
        }

        private void Client_StateChaged(object sender, Client.ConectionState e)
        {
            Dispatcher.BeginInvoke(new Action(() => FireInfo("[StateChaged]", e.ToString())));
        }

        private void Client_OnFileMessageReived(object o, FileMessage fileMessage)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var chatFileMessage = new ChatFileMessage(fileMessage);
                Messages.Add(chatFileMessage);
            }));
        }

        private ChatFileMessage GetTransferingChatFileMessage(string fileId)
        {
            foreach (var t in _transferingFiles)
            {
                if (t.FileId == fileId)
                {
                    return t;
                }
            }
            return null;
        }
        private void Client_FileReived(object sender, FileMessage fileMessage)
        {
            Debug.WriteLine($"Client received file (ID: {fileMessage.FileId}).");
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var t = GetTransferingChatFileMessage(fileMessage.FileId);
                var saveFilePath = _windowServer.GetSaveFilePath(fileMessage.FileName);
                if (saveFilePath == null) return;

                if (t != null)
                {
                    // FIXME!
                    t.Process = 50;
                    Task.Run(async () =>
                    {
                        File.WriteAllBytes(saveFilePath, fileMessage.data);
                        fileMessage.data = null;
                        await Task.Delay(500);
                        Dispatcher.Invoke(() =>
                        {
                            t.Process = 75;
                        });
                        await Task.Delay(500);
                        Dispatcher.Invoke(() =>
                        {
                            t.Process = 100;
                        });
                        await Task.Delay(500);
                        Dispatcher.Invoke(() =>
                        {
                            t.IsTransfering = false;
                            _transferingFiles.Remove(t);
                        });
                    });
                }
            }));
        }
    }
}
