﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SlackConnector.BotHelpers;
using SlackConnector.Connections;
using SlackConnector.Connections.Clients.Channel;
using SlackConnector.Connections.Models;
using SlackConnector.Connections.Sockets;
using SlackConnector.Connections.Sockets.Messages.Inbound;
using SlackConnector.Connections.Sockets.Messages.Outbound;
using SlackConnector.EventHandlers;
using SlackConnector.Exceptions;
using SlackConnector.Extensions;
using SlackConnector.Models;

namespace SlackConnector
{
    internal class SlackConnection : ISlackConnection
    {
        private readonly IConnectionFactory _connectionFactory;
        private readonly IMentionDetector _mentionDetector;
        private IWebSocketClient _webSocketClient;

        private Dictionary<string, SlackChatHub> _connectedHubs { get; set; }
        public IReadOnlyDictionary<string, SlackChatHub> ConnectedHubs => _connectedHubs;

        private Dictionary<string, SlackUser> _userCache { get; set; }
        public IReadOnlyDictionary<string, SlackUser> UserCache => _userCache;

        [Obsolete("Please use UserCache", true)]
        public IReadOnlyDictionary<string, SlackUser> UserNameCache { get; set; }

        public bool IsConnected => ConnectedSince.HasValue;
        public DateTime? ConnectedSince { get; private set; }
        public string SlackKey { get; private set; }

        public ContactDetails Team { get; private set; }
        public ContactDetails Self { get; private set; }

        public SlackConnection(IConnectionFactory connectionFactory, IMentionDetector mentionDetector)
        {
            _connectionFactory = connectionFactory;
            _mentionDetector = mentionDetector;
        }

        public void Initialise(ConnectionInformation connectionInformation)
        {
            SlackKey = connectionInformation.SlackKey;
            Team = connectionInformation.Team;
            Self = connectionInformation.Self;
            _userCache = connectionInformation.Users;
            _connectedHubs = connectionInformation.SlackChatHubs;

            _webSocketClient = connectionInformation.WebSocket;
            _webSocketClient.OnClose += (sender, args) =>
            {
                ConnectedSince = null;
                RaiseOnDisconnect();
            };

            _webSocketClient.OnMessage += async (sender, message) => await ListenTo(message);

            ConnectedSince = DateTime.Now;
        }

        private Task ListenTo(InboundMessage inboundMessage)
        {
            if (inboundMessage == null)
            {
                return Task.CompletedTask;
            }

            switch (inboundMessage.MessageType)
            {
                case MessageType.Message: return HandleMessage((ChatMessage)inboundMessage);
                case MessageType.Group_Joined: return HandleGroupJoined((GroupJoinedMessage)inboundMessage);
                case MessageType.Channel_Joined: return HandleChannelJoined((ChannelJoinedMessage)inboundMessage);
            }

            return Task.CompletedTask;
        }

        private Task HandleMessage(ChatMessage inboundMessage)
        {
            if (string.IsNullOrEmpty(inboundMessage.User))
                return Task.CompletedTask;

            if(!string.IsNullOrEmpty(Self.Id) && inboundMessage.User == Self.Id)
                return Task.CompletedTask;
            
            var message = new SlackMessage
            {
                User = GetMessageUser(inboundMessage.User),
                TimeStamp = inboundMessage.TimeStamp,
                Text = inboundMessage.Text,
                ChatHub = inboundMessage.Channel == null ? null : _connectedHubs[inboundMessage.Channel],
                RawData = inboundMessage.RawData,
                MentionsBot = _mentionDetector.WasBotMentioned(Self.Name, Self.Id, inboundMessage.Text)
            };

            return RaiseMessageReceived(message);
        }

        private Task HandleGroupJoined(GroupJoinedMessage inboundMessage)
        {
            string channelId = inboundMessage?.Channel?.Id;
            if (channelId == null) return Task.CompletedTask;

            var hub = inboundMessage.Channel.ToChatHub();
            _connectedHubs[channelId] = hub;

            return RaiseChatHubJoined(hub);
        }

        private Task HandleChannelJoined(ChannelJoinedMessage inboundMessage)
        {
            string channelId = inboundMessage?.Channel?.Id;
            if (channelId == null) return Task.CompletedTask;

            var hub = inboundMessage.Channel.ToChatHub();
            _connectedHubs[channelId] = hub;

            return RaiseChatHubJoined(hub);
        }

        private SlackUser GetMessageUser(string userId)
        {
            return UserCache.ContainsKey(userId) ?
                UserCache[userId] :
                new SlackUser { Id = userId, Name = string.Empty };
        }

        public void Disconnect()
        {
            if (_webSocketClient != null && _webSocketClient.IsAlive)
            {
                _webSocketClient.Close();
            }
        }

        public async Task Say(BotMessage message)
        {
            if (string.IsNullOrEmpty(message.ChatHub?.Id))
            {
                throw new MissingChannelException("When calling the Say() method, the message parameter must have its ChatHub property set.");
            }

            var client = _connectionFactory.CreateChatClient();
            await client.PostMessage(SlackKey, message.ChatHub.Id, message.Text, message.Attachments);
        }

        public async Task<IEnumerable<SlackChatHub>> GetChannels()
        {
            IChannelClient client = _connectionFactory.CreateChannelClient();
            var channels = await client.GetChannels(SlackKey);
            var groups = await client.GetGroups(SlackKey);

            var fromChannels = channels.Select(c => c.ToChatHub());
            var fromGroups = groups.Select(g => g.ToChatHub());
            return fromChannels.Concat(fromGroups);
        }

        public async Task<IEnumerable<SlackUser>> GetUsers()
        {
            IChannelClient client = _connectionFactory.CreateChannelClient();
            var users = await client.GetUsers(SlackKey);

            return users.Select(u => u.ToSlackUser());
        }

        //TODO: Cache newly created channel, and return if already exists
        public async Task<SlackChatHub> JoinDirectMessageChannel(string user)
        {
            if (string.IsNullOrEmpty(user))
            {
                throw new ArgumentNullException(nameof(user));
            }

            IChannelClient client = _connectionFactory.CreateChannelClient();
            Channel channel = await client.JoinDirectMessageChannel(SlackKey, user);

            return new SlackChatHub
            {
                Id = channel.Id,
                Name = channel.Name,
                Type = SlackChatHubType.DM
            };
        }

        public async Task IndicateTyping(SlackChatHub chatHub)
        {
            var message = new TypingIndicatorMessage
            {
                Channel = chatHub.Id,
                Type = "typing"
            };

            await _webSocketClient.SendMessage(message);
        }

        public async Task Ping()
        {
            var message = new PingMessage
            {
                Type = "ping"
            };

            await _webSocketClient.SendMessage(message);
        }

        public event DisconnectEventHandler OnDisconnect;
        private void RaiseOnDisconnect()
        {
            OnDisconnect?.Invoke();
        }

        public event MessageReceivedEventHandler OnMessageReceived;
        private async Task RaiseMessageReceived(SlackMessage message)
        {
            var e = OnMessageReceived;
            if (e != null)
            {
                try
                {
                    await e(message);
                }
                catch (Exception)
                {

                }
            }
        }

        public event ChatHubJoinedEventHandler OnChatHubJoined;
        private async Task RaiseChatHubJoined(SlackChatHub hub)
        {
            var e = OnChatHubJoined;

            if (e != null)
            {
                try
                {
                    await e(hub);
                }
                catch
                {
                }
            }
        }
    }
}
