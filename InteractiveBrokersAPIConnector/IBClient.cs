using log4net;
using Net.Teirlinck.FX.Data;
using Net.Teirlinck.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI
{
    public class IBClient : IDisposable
    {
        private static ILog logger = LogManager.GetLogger(nameof(IBClient));

        public int ClientID { get; private set; }

        public string ClientName { get; private set; }

        public string IBHost { get; private set; }

        public int IBPort { get; private set; }

        public IBClientRequestsManager RequestManager { get; private set; }
        public IBClientResponsesManager ResponseManager { get; private set; }

        public event Action<APIError> APIErrorReceived;
        public event Action IBConnectionLost;
        public event Action IBConnectionEstablished;

        private Timer connectionMonitorTimer = null;
        private bool previousConnectedStatus = false;

        private string[] apiErrorsToIgnore = new string[] { "Already Connected", "Not connected" };

        private Dictionary<int, APIErrorCode> ibApiErrorCodesDict = null;

        public IBClient(int clientID, string clientName, string ibHost, int ibPort, IEnumerable<APIErrorCode> ibApiErrorCodes, CancellationToken stopRequestedCt)
        {
            ClientID = clientID;
            ClientName = clientName;

            IBHost = ibHost;
            IBPort = ibPort;

            if (!ibApiErrorCodes.IsNullOrEmpty())
                ibApiErrorCodesDict = ibApiErrorCodes.ToDictionary(code => code.Code, code => code);
            else
            {
                logger.Error("Not setting up IB API error codes dictionary");
                ibApiErrorCodesDict = new Dictionary<int, APIErrorCode>();
            }

            RequestManager = new IBClientRequestsManager();

            ResponseManager = RequestManager.GetResponseManager();
            ResponseManager.ErrorMessageReceived += ResponseManager_ErrorMessageReceived;

            logger.Info($"Attempting first connection of {ToString()}");
            Task<bool> initialConnectTask = Task.Run<bool>(() =>
            {
                return Connect(stopRequestedCt);
            });

            initialConnectTask.Wait(2000, stopRequestedCt);

            connectionMonitorTimer = new Timer((state) =>
            {
                try
                {
                    if (!IsConnected())
                    {
                        // 1. If the connection just broke, send out a notification
                        if (previousConnectedStatus)
                        {

                            logger.Error($"Client {ClientID} lost connection to IB on {IBHost}:{IBPort}. Will attempt to reconnect");

                            IBConnectionLost?.Invoke();

                            previousConnectedStatus = false;
                        }

                        // 2. Attempt to reconnect
                        Task<bool> connectTask = Task.Run<bool>(() =>
                        {
                            return Connect(stopRequestedCt);
                        });

                        connectTask.Wait(2000, stopRequestedCt);

                        if (!connectTask.IsCompleted || !connectTask.Result)
                            logger.Warn($"Client {ClientID} unable to connect on {IBHost}:{IBPort}. Trying again in 10 seconds");
                    }
                    else
                    {
                        if (!previousConnectedStatus)
                        {
                            previousConnectedStatus = true;

                            logger.Info($"Client {ClientID} successfully connected to IB on {IBHost}:{IBPort}");

                            IBConnectionEstablished?.Invoke();
                        }
                        else
                            logger.Info($"{ClientName} is still connected to IB on {IBHost}:{IBPort}");
                    }
                }
                catch (OperationCanceledException)
                {
                    logger.Error("Not checking connection status. Operation cancelled");
                }
            }, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        }

        private bool Connect(CancellationToken stopRequestedCt)
        {
            try
            {
                stopRequestedCt.ThrowIfCancellationRequested();

                RequestManager.Connect(ClientID, IBHost, IBPort);

                return true;
            }
            catch (OperationCanceledException)
            {
                logger.Error("Not attempting to connect to IB. Operation cancelled");

                return false;
            }
        }

        public bool IsConnected()
        {
            return RequestManager.ClientSocket.IsConnected();
        }

        private void ResponseManager_ErrorMessageReceived(APIError apiError)
        {
            if (apiError.InnerException != null)
            {
                if (apiError.InnerException is SocketException || apiError.InnerException is EndOfStreamException)
                    return;
            }
            else if (apiErrorsToIgnore.Contains(apiError.ErrorMessage))
                return;

            // Set error code
            if (ibApiErrorCodesDict.ContainsKey(apiError.ErrorCodeInt))
            {
                apiError.ErrorCode = ibApiErrorCodesDict[apiError.ErrorCodeInt];
                logger.Debug($"Setup ErrorCode object for apiError: {apiError.ErrorCode} {apiError.ErrorCode.Description}");
            }
            else
                logger.Warn($"Unable to set IB API Error Code from int error code {apiError.ErrorCodeInt}");

            APIErrorReceived?.Invoke(apiError);
        }

        private void Disconnect()
        {
            logger.Info($"Disconnecting client {ClientID} from IB Gateway");

            ResponseManager.ConnectionClosed += () => { logger.Info($"Successfully closed connection of client {ClientID} to IB Gateway"); };

            RequestManager.ClientSocket.eDisconnect();
        }

        public override string ToString()
        {
            return $"Client {ClientID}: {ClientName} ({IBHost}:{IBPort})";
        }

        public void Dispose()
        {
            logger.Info($"Disposing {ClientName}");

            // 1. Terminate the connection monitor timer
            try { connectionMonitorTimer?.Dispose(); connectionMonitorTimer = null; } catch { }

            // 2. Disconnect
            Disconnect();
        }
    }
}
