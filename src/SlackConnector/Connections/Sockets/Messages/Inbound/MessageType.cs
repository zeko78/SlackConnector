﻿namespace SlackConnector.Connections.Sockets.Messages.Inbound
{
    public enum MessageType
    {
        Unknown = 0,
        Message,
        Group_Joined,
        Channel_Joined,
        Im_Created,
        Team_Join
    }
}
