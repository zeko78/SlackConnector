using System;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SlackConnector.Connections.Sockets.Messages.Inbound;
using SlackConnector.Connections.Sockets.Messages.Outbound;
using SuperSocket.ClientEngine;
using WebSocket4Net;
using WebSocket = WebSocket4Net.WebSocket;

namespace SlackConnector.Connections.Sockets
{
    internal class WebSocketClient4Net : IWebSocketClient
    {
        private readonly IMessageInterpreter _interpreter;
        private readonly WebSocket _webSocket;
        private int _currentMessageId;

        public bool IsAlive => _webSocket.State == WebSocketState.Open;

        public event EventHandler<InboundMessage> OnMessage;
        public event EventHandler OnClose;

        public WebSocketClient4Net(IMessageInterpreter interpreter, string url, ProxySettings proxySettings)
        {
            _interpreter = interpreter;
            _webSocket = new WebSocket(url);
            _webSocket.MessageReceived += WebSocketOnMessage;
            _webSocket.Closed += (sender, args) => OnClose?.Invoke(sender, args);
            
            //_webSocket.Log.Level = GetLoggingLevel();
            //_webSocket.EmitOnPing = true;

            //if (proxySettings != null)
            //{
            //    _webSocket.SetProxy(proxySettings.Url, proxySettings.Username, proxySettings.Password);
            //}
        }

        public async Task Connect()
        {
            var taskSource = new TaskCompletionSource<bool>();
            EventHandler<ErrorEventArgs> onError = (sender, args) => taskSource.TrySetException(args.Exception);

            _webSocket.Opened += (sender, args) => { taskSource.SetResult(true); _webSocket.Error -= onError; };
            _webSocket.Error += onError;
            _webSocket.Open();
            await taskSource.Task;
        }
        
        private void WebSocketOnMessage(object sender, MessageReceivedEventArgs args)
        {
            string messageJson = args?.Message ?? "";
            var inboundMessage = _interpreter.InterpretMessage(messageJson);
            OnMessage?.Invoke(sender, inboundMessage);
        }
        
        public Task SendMessage(BaseMessage message)
        {
            System.Threading.Interlocked.Increment(ref _currentMessageId);
            message.Id = _currentMessageId;
            string json = JsonConvert.SerializeObject(message);

            //var taskSource = new TaskCompletionSource<bool>();

            _webSocket.Send(json);

            //, sent =>
            //{
            //    if (sent)
            //    {
            //        taskSource.SetResult(true);
            //    }
            //    else
            //    {
            //        taskSource.TrySetException(new Exception("Error occured while sending message"));
            //    }
            //});

            return Task.CompletedTask;
        }

        public void Close()
        {
            _webSocket.Close();
        }
    }
}