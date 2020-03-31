﻿using Microsoft.Extensions.Logging;
using StreamCore.Interfaces;
using StreamCore.Models;
using StreamCore.Models.Twitch;
using StreamCore;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace StreamCore.Services.Twitch
{
    public class TwitchService : StreamingServiceBase, IStreamingService
    {
        private ConcurrentDictionary<string, IChatChannel> _channels = new ConcurrentDictionary<string, IChatChannel>();
        public ReadOnlyDictionary<string, IChatChannel> Channels;

        public TwitchService(ILogger<TwitchService> logger, TwitchMessageParser messageParser, TwitchDataProvider twitchDataProvider, IWebSocketService websocketService, IWebLoginProvider webLoginProvider, IUserAuthManager authManager, ISettingsProvider settingsProvider, Random rand)
        {
            _logger = logger;
            _messageParser = messageParser;
            _twitchDataProvider = twitchDataProvider;
            _websocketService = websocketService;
            _webLoginProvider = webLoginProvider;
            _authManager = authManager;
            _settingsProvider = settingsProvider;
            _rand = rand;

            Channels = new ReadOnlyDictionary<string, IChatChannel>(_channels);

            _authManager.OnCredentialsUpdated += _authManager_OnCredentialsUpdated;
            _websocketService.OnOpen += _websocketService_OnOpen;
            _websocketService.OnClose += _websocketService_OnClose;
            _websocketService.OnError += _websocketService_OnError;
            _websocketService.OnMessageReceived += _websocketService_OnMessageReceived;
        }

        private void _authManager_OnCredentialsUpdated(LoginCredentials credentials)
        {
            _logger.LogInformation($"Twitch_OAuthToken: {credentials.Twitch_OAuthToken}");
            if (_isStarted)
            {
                Start(true);
            }
        }

        private ILogger _logger;
        private TwitchMessageParser _messageParser;
        private TwitchDataProvider _twitchDataProvider;
        private IWebSocketService _websocketService;
        private IWebLoginProvider _webLoginProvider;
        private IUserAuthManager _authManager;
        private ISettingsProvider _settingsProvider;
        private Random _rand;
        private bool _isStarted = false;
        private string _loggedInUserName = "@";
        private string _anonUsername;
        private object _messageReceivedLock = new object();
        private string _userName { get => string.IsNullOrEmpty(_authManager.Credentials.Twitch_OAuthToken) ? _anonUsername : _loggedInUserName; }
        private string _oAuthToken { get => string.IsNullOrEmpty(_authManager.Credentials.Twitch_OAuthToken) ? "" : _authManager.Credentials.Twitch_OAuthToken; }

        internal void Start(bool forceReconnect = false)
        {
            _isStarted = true;
            _websocketService.Connect("wss://irc-ws.chat.twitch.tv:443", forceReconnect);
        }

        internal void Stop()
        {
            _isStarted = false;
            _websocketService.Disconnect();
        }

        private void _websocketService_OnMessageReceived(Assembly assembly, string message)
        {
            lock (_messageReceivedLock)
            {
                //_logger.LogInformation("RawMessage: " + message);
                if (_messageParser.ParseRawMessage(message, _channels, out var parsedMessages))
                {
                    foreach (TwitchMessage twitchMessage in parsedMessages)
                    {
                        var twitchChannel = (twitchMessage.Channel as TwitchChannel);
                        if (twitchChannel.Roomstate == null)
                        {
                            twitchChannel.Roomstate = _channels.TryGetValue(twitchMessage.Channel.Id, out var channel) ? (channel as TwitchChannel).Roomstate : new TwitchRoomstate();
                        }
                        switch (twitchMessage.Type)
                        {
                            case "PING":
                                SendRawMessage("PONG :tmi.twitch.tv");
                                continue;
                            case "376":  // successful login
                                _twitchDataProvider.TryRequestGlobalResources();
                                _loggedInUserName = twitchMessage.Channel.Id;
                                _logger.LogInformation($"Logged into Twitch as {_loggedInUserName}");
                                //JoinChannel("brian91292"); // TODO: allow user to set channel
                                _websocketService.ReconnectDelay = 500;
                                _onLoginCallbacks?.InvokeAll(assembly, this, _logger);
                                continue;
                            case "NOTICE":
                                switch (twitchMessage.Message)
                                {
                                    case "Login authentication failed":
                                    case "Invalid NICK":
                                        _websocketService.Disconnect();
                                        break;
                                }
                                goto case "PRIVMSG";
                            case "PRIVMSG":
                                _onTextMessageReceivedCallbacks.InvokeAll(assembly, twitchMessage, _logger);
                                continue;
                            case "JOIN":
                                if (twitchMessage.Sender.Name == _userName)
                                {
                                    if (!_channels.ContainsKey(twitchMessage.Channel.Id))
                                    {
                                        _channels[twitchMessage.Channel.Id] = twitchMessage.Channel.AsTwitchChannel();
                                        _logger.LogInformation($"Added channel {twitchMessage.Channel.Id} to the channel list.");
                                        _onJoinRoomCallbacks.InvokeAll(assembly, twitchMessage.Channel, _logger);
                                    }
                                }
                                continue;
                            case "PART":
                                if (twitchMessage.Sender.Name == _userName)
                                {
                                    if (_channels.TryRemove(twitchMessage.Channel.Id, out var channel))
                                    {
                                        _twitchDataProvider.TryReleaseChannelResources(twitchMessage.Channel);
                                        _logger.LogInformation($"Removed channel {channel.Id} from the channel list.");
                                        _onLeaveRoomCallbacks.InvokeAll(assembly, twitchMessage.Channel, _logger);
                                    }
                                }
                                continue;
                            case "ROOMSTATE":
                                _twitchDataProvider.TryRequestChannelResources(twitchMessage.Channel);
                                _channels[twitchMessage.Channel.Id] = twitchMessage.Channel;
                                _onRoomStateUpdatedCallbacks.InvokeAll(assembly, twitchMessage.Channel, _logger);
                                continue;
                            case "MODE":
                            case "NAMES":
                            case "CLEARCHAT":
                            case "CLEARMSG":
                            case "HOSTTARGET":
                            case "RECONNECT":
                            case "USERNOTICE":
                            case "USERSTATE":
                            case "GLOBALUSERSTATE":
                                _logger.LogInformation($"No handler exists for type {twitchMessage.Type}");
                                continue;
                        }
                    }
                }
            }
        }

        private void _websocketService_OnClose()
        {
            _loggedInUserName = "@";
            _logger.LogInformation("Twitch connection closed");
        }

        private void _websocketService_OnError()
        {
            _loggedInUserName = "@";
            _logger.LogError("An error occurred in Twitch connection");
        }

        private void _websocketService_OnOpen()
        {
            _logger.LogInformation("Twitch connection opened");
            _websocketService.SendMessage("CAP REQ :twitch.tv/tags twitch.tv/commands twitch.tv/membership");
            _anonUsername = $"justinfan{_rand.Next(10000, 1000000)}".ToLower();
            TryLogin();
        }

        private void TryLogin()
        {
            _logger.LogInformation("Trying to login!");
            if (!string.IsNullOrEmpty(_oAuthToken))
            {
                _websocketService.SendMessage($"PASS {_oAuthToken}");
            }
            _websocketService.SendMessage($"NICK {_userName}");
        }

        private void SendRawMessage(Assembly assembly, string rawMessage, bool forwardToSharedClients = false)
        {
            if (_websocketService.IsConnected)
            {
                _websocketService.SendMessage(rawMessage);
                if (forwardToSharedClients)
                {
                    _websocketService_OnMessageReceived(assembly, rawMessage);
                }
            }
            else
            {
                _logger.LogWarning("WebSocket service is not connected!");
            }
        }

        /// <summary>
        /// Sends a raw message to the Twitch server
        /// </summary>
        /// <param name="rawMessage">The raw message to send.</param>
        /// <param name="forwardToSharedClients">
        /// Whether or not the message should also be sent to other clients in the assembly that implement StreamCore, or only to the Twitch server.<br/>
        /// This should only be set to true if the Twitch server would rebroadcast this message to other external clients as a response to the message.
        /// </param>
        public void SendRawMessage(string rawMessage, bool forwardToSharedClients = false)
        {
            SendRawMessage(Assembly.GetCallingAssembly(), rawMessage, forwardToSharedClients);
        }

        public void SendTextMessage(string message, string channel)
        {
            SendRawMessage(Assembly.GetCallingAssembly(), $"PRIVMSG #{channel} :{message}", true);
        }

        public void SendCommand(string command, string channel)
        {
            SendRawMessage(Assembly.GetCallingAssembly(), $"PRIVMSG #{channel} :/{command}");
        }

        public void JoinChannel(string channel)
        {
            _logger.LogInformation($"Trying to join channel #{channel}");
            SendRawMessage(Assembly.GetCallingAssembly(), $"JOIN #{channel.ToLower()}");
        }

        public void PartChannel(string channel)
        {
            SendRawMessage(Assembly.GetCallingAssembly(), $"PART #{channel.ToLower()}");
        }
    }
}
