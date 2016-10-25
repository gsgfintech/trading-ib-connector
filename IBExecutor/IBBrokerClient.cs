using System;
using System.Threading.Tasks;
using System.Threading;
using log4net;
using System.Collections.Generic;
using System.Linq;
using Capital.GSG.FX.MarketDataService.Connector;
using Capital.GSG.FX.FXConverter;
using Capital.GSG.FX.IBData.Service.Connector;
using Capital.GSG.FX.Data.Core.SystemData;
using Capital.GSG.FX.Utils.Core;
using Capital.GSG.FX.Data.Core.OrderData;
using Capital.GSG.FX.Trading.Executor.Core;
using Capital.GSG.FX.Data.Core.ContractData;
using Capital.GSG.FX.IBData;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI.Executor
{
    public class BrokerClient : IBrokerClient
    {
        private static ILog logger = LogManager.GetLogger(nameof(BrokerClient));

        private const string ClientNameKey = "Name";
        private const string ClientNumberKey = "ClientNumber";
        private const string ClientHostKey = "Host";
        private const string ClientPortKey = "Port";
        private const string ClientTradingAccountKey = "TradingAccount";
        private const string IBDataServiceEndpointKey = "IBDataServiceEndpoint";
        private const string IsConnectedKey = "IsConnected";
        private const string MessageKey = "Message";
        private const string StatusKey = "Status";
        private const string SuccessKey = "Success";

        private static BrokerClient _instance;

        private readonly IBClient ibClient;
        private readonly string clientName;
        private readonly CancellationToken stopRequestedCt;

        private readonly IBrokerClientType brokerClientType;
        public IBrokerClientType BrokerClientType { get { return brokerClientType; } }

        private readonly ITradingExecutorRunner tradingExecutorRunner;
        public ITradingExecutorRunner TradingExecutorRunner { get { return tradingExecutorRunner; } }

        private IBMarketDataProvider marketDataProvider;
        public IMarketDataProvider MarketDataProvider { get { return marketDataProvider; } }

        private IBNewsBulletinProvider newsBulletinProvider;
        public INewsBulletinProvider NewsBulletinProvider { get { return newsBulletinProvider; } }

        private IBOrderExecutor orderExecutor;
        public IOrderExecutor OrderExecutor { get { return orderExecutor; } }

        private IBPositionsExecutor positionExecutor;
        public IPositionExecutor PositionExecutor { get { return positionExecutor; } }

        private IBTradesExecutor tradesExecutor;
        public ITradesExecutor TradesExecutor { get { return tradesExecutor; } }

        private readonly string monitoringEndpoint;
        private readonly string ibDataServiceEndpoint;

        private SystemStatus Status { get; set; }
        public event Action<SystemStatus> StatusUpdated;

        public event Action<Alert> AlertReceived;

        public event Action StopComplete;

        private Timer statusUpdateTimer = null;
        private Timer twsRestartTimer = null;
        private object twsRestartTimerLocker = new object();

        private BrokerClient(IBrokerClientType clientType, ITradingExecutorRunner tradingExecutorRunner, int clientID, string clientName, string socketHost, int socketPort, IEnumerable<APIErrorCode> ibApiErrorCodes, string monitoringEndpoint, string ibDataServiceEndpoint, CancellationToken stopRequestedCt)
        {
            this.brokerClientType = clientType;
            this.clientName = clientName;
            this.tradingExecutorRunner = tradingExecutorRunner;
            this.monitoringEndpoint = monitoringEndpoint;
            this.ibDataServiceEndpoint = ibDataServiceEndpoint;

            this.stopRequestedCt = stopRequestedCt;

            Status = new SystemStatus(clientName);

            ibClient = new IBClient(clientID, clientName, socketHost, socketPort, ibApiErrorCodes, stopRequestedCt);
            ibClient.APIErrorReceived += IbClient_APIErrorReceived;
            ibClient.IBConnectionEstablished += () => UpdateStatus(IsConnectedKey, true, SystemStatusLevel.GREEN);
            ibClient.IBConnectionLost += () => UpdateStatus(IsConnectedKey, false, SystemStatusLevel.RED);

            // Throttle status updates to one every five seconds
            // Delay the first status update by 3 seconds, otherwise the client is already connected before the trade engine has had time to wire up the "StatusUpdated" event listener
            statusUpdateTimer = new Timer(state => SendStatusUpdate(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5));
        }

        public static async Task<IBrokerClient> SetupBrokerClient(IBrokerClientType clientType, ITradingExecutorRunner tradingExecutorRunner, Dictionary<string, object> clientConfig, IFxConverter fxConverter, MDConnector mdConnector, string monitoringEndpoint, CancellationToken stopRequestedCt, bool logTicks, IEnumerable<Contract> ibContracts)
        {
            if (clientConfig == null)
                throw new ArgumentNullException(nameof(clientConfig));

            #region ClientNameKey
            if (!clientConfig.ContainsKey(ClientNameKey))
                throw new ArgumentNullException(nameof(ClientNameKey));

            string name = clientConfig[ClientNameKey]?.ToString();

            if (string.IsNullOrEmpty(name))
                throw new ArgumentException($"Failed to parse config key {ClientNameKey} as string");
            #endregion

            #region ClientNumberKey
            if (!clientConfig.ContainsKey(ClientNumberKey))
                throw new ArgumentNullException(nameof(ClientNumberKey));

            int number;
            if (!int.TryParse(clientConfig[ClientNumberKey]?.ToString(), out number))
                throw new ArgumentException($"Failed to parse config key {ClientNumberKey} as int");
            #endregion

            #region ClientHostKey
            if (!clientConfig.ContainsKey(ClientHostKey))
                throw new ArgumentNullException(nameof(ClientHostKey));

            string host = clientConfig[ClientHostKey]?.ToString();

            if (string.IsNullOrEmpty(host))
                throw new ArgumentException($"Failed to parse config key {ClientHostKey} as string");
            #endregion

            #region ClientPortKey
            if (!clientConfig.ContainsKey(ClientPortKey))
                throw new ArgumentNullException(nameof(ClientPortKey));

            int port;
            if (!int.TryParse(clientConfig[ClientPortKey]?.ToString(), out port))
                throw new ArgumentException($"Failed to parse config key {ClientPortKey} as int");
            #endregion

            #region ClientTradingAccountKey
            if (!clientConfig.ContainsKey(ClientTradingAccountKey))
                throw new ArgumentNullException(nameof(ClientTradingAccountKey));

            string tradingAccount = clientConfig[ClientTradingAccountKey]?.ToString();

            if (string.IsNullOrEmpty(tradingAccount))
                throw new ArgumentException($"Failed to parse config key {ClientTradingAccountKey} as string");
            #endregion

            #region IBDataServiceEndpoint
            if (!clientConfig.ContainsKey(IBDataServiceEndpointKey) || string.IsNullOrEmpty(clientConfig[IBDataServiceEndpointKey]?.ToString()))
                throw new ArgumentNullException(nameof(IBDataServiceEndpointKey));

            string ibDataServiceEndpoint = clientConfig[IBDataServiceEndpointKey].ToString();
            #endregion

            if (fxConverter == null)
                throw new ArgumentNullException(nameof(fxConverter));

            logger.Debug($"Loading IB client config for {name}");
            logger.Debug($"ClientNumber: {number}");
            logger.Debug($"IBHost: {host}");
            logger.Debug($"IBPort: {port}");
            logger.Debug($"TradingAccount: {tradingAccount}");
            logger.Debug($"IBDataServiceEndpoint: {ibDataServiceEndpoint}");

            APIErrorCodesConnector errorCodesConnector = APIErrorCodesConnector.GetConnector(ibDataServiceEndpoint);
            List<APIErrorCode> ibApiErrorCodes = await errorCodesConnector.GetAll(stopRequestedCt);

            _instance = new BrokerClient(clientType, tradingExecutorRunner, number, name, host, port, ibApiErrorCodes, monitoringEndpoint, ibDataServiceEndpoint, stopRequestedCt);

            logger.Info("Setup broker client complete. Wait for 2 seconds before setting up executors");
            Task.Delay(TimeSpan.FromSeconds(2)).Wait();

            await _instance.SetupExecutors(fxConverter, mdConnector, tradingAccount, logTicks, stopRequestedCt, ibContracts);

            return _instance;
        }

        private async Task SetupExecutors(IFxConverter fxConverter, MDConnector mdConnector, string tradingAccount, bool logTicks, CancellationToken stopRequestedCt, IEnumerable<Contract> ibContracts)
        {
            if (brokerClientType != IBrokerClientType.MarketData)
            {
                logger.Info("Setting up orders executor, positions executor and trades executor");

                orderExecutor = IBOrderExecutor.SetupOrderExecutor(this, ibClient, fxConverter, mdConnector, tradingExecutorRunner, monitoringEndpoint, ibContracts, stopRequestedCt);
                positionExecutor = IBPositionsExecutor.SetupIBPositionsExecutor(ibClient, tradingAccount, fxConverter, stopRequestedCt);
                tradesExecutor = new IBTradesExecutor(this, ibClient, fxConverter, stopRequestedCt);
            }

            if (brokerClientType != IBrokerClientType.Trading)
            {
                logger.Info("Setting up market data provider and news bulletins provider");

                marketDataProvider = await IBMarketDataProvider.SetupIBMarketDataProvider(this, ibClient, ibDataServiceEndpoint, logTicks, stopRequestedCt);
                newsBulletinProvider = new IBNewsBulletinProvider(ibClient, stopRequestedCt);
            }
        }

        private async void IbClient_APIErrorReceived(APIError error)
        {
            try
            {
                if (error != null)
                {
                    MarketDataRequest requestDetails = null;

                    string subject = $"{error.ErrorCodeDescription ?? "Unclassified error"} (IB-{error.ErrorCode})";
                    string body = error.ErrorMessage;

                    #region Additional action handlers for specific errors
                    switch (error.ErrorCode)
                    {
                        case 103:
                            subject = $"Duplicate order ID: {error.RequestID}";
                            logger.Info("Received duplicate order ID error. Will notify order executor to increment its next valid order ID");
                            await orderExecutor.RequestNextValidOrderID();
                            break;
                        case 110:
                            subject = $"Limit or stop price of order {error.RequestID} is invalid";
                            body = $"[{error.Level} {error.ErrorCode}] {subject}: {error.ErrorCodeDescription}";
                            break;
                        case 135:
                            subject = $"Order {error.RequestID} is not recognized by TWS";
                            body = $"[{error.Level} {error.ErrorCode}] {subject}. Marking it as cancelled";
                            orderExecutor.OnOrderStatusChangeReceived(error.RequestID, OrderStatusCode.ApiCanceled, null, null, null, -1, null, null, ibClient.ClientID, subject);
                            break;
                        case 161:
                            subject = $"Order {error.RequestID} is not cancellable";
                            body = $"[{error.Level} {error.ErrorCode}] {error.ErrorCodeDescription} {error.ErrorMessage?.Split('=').LastOrDefault()}, order ID: {error.RequestID}";
                            orderExecutor.StopTradingStrategyForOrder(error.RequestID, $"{error.ErrorCodeDescription} {error.ErrorMessage?.Split('=').LastOrDefault()}, order ID: {error.RequestID}");
                            break;
                        case 200:
                        case 300:
                            subject = $"Failed MD request {error.RequestID}";
                            requestDetails = marketDataProvider?.GetRequestDetails(error.RequestID);
                            if (requestDetails != null)
                                body = $"No security definition found for request {requestDetails}";
                            break;
                        case 201:
                            subject = $"Order {error.RequestID} was rejected";
                            body = $"[{error.Level} {error.ErrorCode}] {subject}";
                            break;
                        case 202:
                            subject = $"Order {error.RequestID} was cancelled";
                            body = $"[{error.Level} {error.ErrorCode}] {subject}";
                            break;
                        case 1100:
                        case 1102:
                            subject = error.ErrorCodeDescription;
                            break;
                        case 2103:
                        case 2105:
                            subject = error.ErrorCodeDescription;
                            // Market data connection lost
                            StartTwsRestartTimer();
                            break;
                        case 2104:
                        case 2106:
                            // Market data connection resumed
                            subject = error.ErrorCodeDescription;
                            TerminateTwsRestartTimer();
                            break;
                        case 10147:
                            subject = $"Order {error.RequestID} to cancel is invalid";
                            body = $"[{error.Level} {error.ErrorCode}] {subject}: {error.ErrorCodeDescription}";
                            break;
                        default:
                            break;
                    }
                    #endregion

                    switch (error.Level)
                    {
                        case AlertLevel.DEBUG:
                            logger.Debug($"{clientName}: {body}");
                            break;
                        case AlertLevel.INFO:
                            logger.Info($"{clientName}: {body}");
                            break;
                        case AlertLevel.WARNING:
                            logger.Warn($"{clientName}: {body}");
                            break;
                        case AlertLevel.ERROR:
                            logger.Error($"{clientName}: {body}");
                            break;
                        case AlertLevel.FATAL:
                            logger.Fatal($"{clientName}: {body}");
                            break;
                        default:
                            break;
                    }

                    if (error.RelayToMonitoringInterface)
                        AlertReceived?.Invoke(new Alert() { Level = error.Level, Source = clientName, Subject = subject, Body = body });
                    else
                        logger.Debug($"Not relaying error {error.ErrorCode} to monitoring interface. Flag RelayToMonitoringInterface is set to false");
                }
            }
            catch (Exception ex)
            {
                logger.Error("Failed to process IB error", ex);
            }
        }

        private void StartTwsRestartTimer()
        {
            lock (twsRestartTimerLocker)
            {
                if (twsRestartTimer == null)
                {
                    logger.Warn("Market data connection was lost. Starting TwsRestartTimer");

                    twsRestartTimer = new Timer(TwsRestartTimerCb, null, 5 * 60 * 1000, Timeout.Infinite);
                }
                else
                    logger.Debug("TwsRestartTimer is already instanciated");
            }
        }

        private async void TwsRestartTimerCb(object state)
        {
            string err = "Market data connection has been lost for 5 minutes. Will restart IB client";
            logger.Error(err);

            OnAlert(new Alert() { Level = AlertLevel.FATAL, Source = clientName, Subject = "Restarting TWS", Body = err, Timestamp = DateTimeOffset.Now, AlertId = Guid.NewGuid().ToString() });

            if (await Restart())
                logger.Info("Restarted IB client");
            else
                logger.Error("Failed to restart IB client");
        }

        private void TerminateTwsRestartTimer()
        {
            lock (twsRestartTimerLocker)
            {
                if (twsRestartTimer != null)
                {
                    logger.Info("Market data connection was re-established. Cancelling TwsRestartTimer");

                    try { twsRestartTimer?.Dispose(); twsRestartTimer = null; } catch { }
                }
                else
                    logger.Debug("twsRestartTimer is null. Nothing to terminate");
            }
        }

        public void Dispose()
        {
            logger.Info("Disposing IBBrokerClient");

            statusUpdateTimer?.Dispose();

            MarketDataProvider?.Dispose();
            OrderExecutor?.Dispose();
            PositionExecutor?.Dispose();
            TradesExecutor?.Dispose();

            ibClient?.Dispose();

            UpdateStatus(MessageKey, "Stop complete", SystemStatusLevel.RED);

            SendStatusUpdate();

            logger.Info("Stop complete");

            StopComplete?.Invoke();
        }

        public void OnAlert(Alert alert)
        {
            AlertReceived?.Invoke(alert);
        }

        /// <summary>
        /// Updates the status object but does not send it. Status will be throttled and sent by statusUpdateTimer or by calling SendStatusUpdate directly
        /// </summary>
        /// <param name="attributeName"></param>
        /// <param name="attributeValue"></param>
        /// <param name="attributeLevel"></param>
        internal void UpdateStatus(string attributeName, object attributeValue, SystemStatusLevel attributeLevel)
        {
            if (attributeValue != null)
            {
                if (string.IsNullOrEmpty(attributeName))
                    return;

                var attribute = Status.Attributes.Where(attr => attr.Name == attributeName).FirstOrDefault();

                if (attribute == null)
                    Status.Attributes.Add(new SystemStatusAttribute(attributeName, attributeValue.ToString(), attributeLevel));
                else
                {
                    attribute.Value = attributeValue.ToString();
                    attribute.Level = attributeLevel;
                }
            }
        }

        /// <summary>
        /// Updates the status object but does not send it. Status will be throttled and sent by statusUpdateTimer or by calling SendStatusUpdate directly
        /// </summary>
        /// <param name="attributes">Param 1: attribute key, Param 2: attribute value, Param 3: attribute status level</param>
        internal void UpdateStatus(IEnumerable<SystemStatusAttribute> attributes)
        {
            if (attributes.IsNullOrEmpty())
                return;

            foreach (var attribute in attributes)
                UpdateStatus(attribute.Name, attribute.Value, attribute.Level);
        }

        private void SendStatusUpdate()
        {
            StatusUpdated?.Invoke(Status);
        }

        public async Task<bool> Start()
        {
            await Task.CompletedTask;
            logger.Error("Not implemented");
            return false;
        }

        public async Task<bool> Stop()
        {
            await Task.CompletedTask;
            logger.Error("Not implemented");
            return false;
        }

        public async Task<bool> Restart()
        {
            await Task.CompletedTask;
            logger.Error("Not implemented");
            return false;
        }
    }
}
