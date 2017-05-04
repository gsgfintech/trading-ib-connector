using System;
using System.Threading.Tasks;
using System.Threading;
using log4net;
using System.Collections.Generic;
using System.Linq;
using Capital.GSG.FX.FXConverter;
using Capital.GSG.FX.Data.Core.SystemData;
using Capital.GSG.FX.Utils.Core;
using Capital.GSG.FX.Data.Core.OrderData;
using Capital.GSG.FX.Trading.Executor.Core;
using Capital.GSG.FX.Data.Core.ContractData;
using Capital.GSG.FX.IBData;
using IBData;
using Capital.gsg.FX.IB.TwsService.Connector;
using MarketDataService.Connector;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI.Executor
{
    public class BrokerClient : IBrokerClient
    {
        private static ILog logger = LogManager.GetLogger(nameof(BrokerClient));

        private const string IsConnectedKey = "IsConnected";
        private const string MessageKey = "Message";

        private readonly IBClient ibClient;
        private readonly string clientName;
        private readonly CancellationToken stopRequestedCt;

        public bool IsInstitutionalAccount { get; private set; }

        private readonly IBrokerClientType brokerClientType;
        public IBrokerClientType BrokerClientType { get { return brokerClientType; } }

        private readonly ITradingExecutorRunner tradingExecutorRunner;
        public ITradingExecutorRunner TradingExecutorRunner { get { return tradingExecutorRunner; } }

        private IBHistoricalDataProvider historicalDataProvider;
        public IBHistoricalDataProvider HistoricalDataProvider { get { return historicalDataProvider; } }

        private IBMarketDataProvider marketDataProvider;
        public IMarketDataProvider MarketDataProvider { get { return marketDataProvider; } }

        private IBNewsProvider newsProvider;
        public IBNewsProvider NewsProvider { get { return newsProvider; } }

        private IBNewsBulletinProvider newsBulletinProvider;
        public INewsBulletinProvider NewsBulletinProvider { get { return newsBulletinProvider; } }

        private IBOrderExecutor orderExecutor;
        public IOrderExecutor OrderExecutor { get { return orderExecutor; } }

        private IBTradesExecutor tradesExecutor;
        public ITradesExecutor TradesExecutor { get { return tradesExecutor; } }

        private readonly string monitoringEndpoint;

        public TwsServiceConnector TwsServiceConnector { get; private set; }

        private SystemStatus Status { get; set; }
        public event Action<SystemStatus> StatusUpdated;

        public event Action<Alert> AlertReceived;

        public event Action StopComplete;

        private Timer statusUpdateTimer = null;
        private Timer twsRestartTimer = null;
        private object twsRestartTimerLocker = new object();

        public BrokerClient(IBrokerClientType clientType, ITradingExecutorRunner tradingExecutorRunner, TwsClientConfig clientConfig, TwsServiceConnector twsServiceConnector, IFxConverter fxConverter, MDConnector mdConnector, IEnumerable<Contract> ibContracts, IEnumerable<APIErrorCode> ibApiErrorCodes, string monitoringEndpoint, bool logTicks, CancellationToken stopRequestedCt)
        {
            brokerClientType = clientType;
            clientName = clientConfig.Name;
            this.tradingExecutorRunner = tradingExecutorRunner;
            this.monitoringEndpoint = monitoringEndpoint;

            this.stopRequestedCt = stopRequestedCt;

            TwsServiceConnector = twsServiceConnector;

            Status = new SystemStatus(clientName);

            ibClient = new IBClient(clientConfig.ClientNumber, clientConfig.Name, clientConfig.Host, clientConfig.Port, ibApiErrorCodes, stopRequestedCt);
            ibClient.APIErrorReceived += IbClient_APIErrorReceived;
            ibClient.IBConnectionEstablished += () => UpdateStatus(IsConnectedKey, true, SystemStatusLevel.GREEN);
            ibClient.IBConnectionLost += () => UpdateStatus(IsConnectedKey, false, SystemStatusLevel.RED);

            ibClient.ResponseManager.ManagedAccountsListReceived += ManagedAccountsListReceived;

            // Throttle status updates to one every five seconds
            // Delay the first status update by 3 seconds, otherwise the client is already connected before the trade engine has had time to wire up the "StatusUpdated" event listener
            statusUpdateTimer = new Timer(state => SendStatusUpdate(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5));

            logger.Info("Setup broker client complete. Wait for 2 seconds before setting up executors");
            Task.Delay(TimeSpan.FromSeconds(2)).Wait();

            SetupExecutors(fxConverter, mdConnector, logTicks, stopRequestedCt, ibContracts);
        }

        private void ManagedAccountsListReceived(string accountsStr)
        {
            if (!string.IsNullOrEmpty(accountsStr))
            {
                var accounts = accountsStr.Split(',').Where(a => !string.IsNullOrEmpty(a));

                if (accounts.Count() > 1)
                {
                    logger.Info($"The user connected to this TWS manages {accounts.Count()} accounts ({string.Join(", ", accounts)}): this is an institutional account");
                    IsInstitutionalAccount = true;
                }
                else
                {
                    logger.Info($"The user connected to this TWS only manages one account ({accountsStr}): this is an individual account");
                    IsInstitutionalAccount = false;
                }

                ibClient.ResponseManager.ManagedAccountsListReceived -= ManagedAccountsListReceived; // We only need to set this once
            }
        }

        private void SetupExecutors(IFxConverter fxConverter, MDConnector mdConnector, bool logTicks, CancellationToken stopRequestedCt, IEnumerable<Contract> ibContracts)
        {
            if (brokerClientType != IBrokerClientType.MarketData)
            {
                logger.Info("Setting up orders executor, positions executor and trades executor");

                orderExecutor = IBOrderExecutor.SetupOrderExecutor(this, ibClient, fxConverter, mdConnector, tradingExecutorRunner, monitoringEndpoint, ibContracts, stopRequestedCt);
                tradesExecutor = new IBTradesExecutor(this, ibClient, fxConverter, stopRequestedCt);
            }

            if (brokerClientType != IBrokerClientType.Trading)
            {
                logger.Info("Setting up market data provider and news bulletins provider");

                historicalDataProvider = new IBHistoricalDataProvider(ibClient, ibContracts, stopRequestedCt);
                marketDataProvider = new IBMarketDataProvider(this, ibClient, ibContracts, logTicks, stopRequestedCt);
                newsProvider = new IBNewsProvider(ibClient, stopRequestedCt);
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
                    AlertLevel level = error.Level;
                    bool relayToMonitoring = error.RelayToMonitoringInterface;

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
                            body = $"[{error.Level} {error.ErrorCode}] {subject}: {body}";
                            orderExecutor.OnOrderStatusChangeReceived(error.RequestID, OrderStatusCode.ApiCanceled, null, null, null, -1, null, null, ibClient.ClientID, subject);
                            break;
                        case 202:
                            subject = $"Order {error.RequestID} was cancelled";
                            body = $"[{error.Level} {error.ErrorCode}] {subject}";
                            break;
                        case 1100: // TWS<->IB connection broken
                            subject = error.ErrorCodeDescription;
                            if (orderExecutor?.HandleTradingDisconnection() != true)
                            {
                                // Downgrade to INFO and do not relay to monitoring
                                level = AlertLevel.INFO;
                                relayToMonitoring = false;
                            }
                            break;
                        case 1102: // TWS<->IB connection restored
                            subject = error.ErrorCodeDescription;
                            if (orderExecutor?.HandleTradingReconnection() != true)
                            {
                                // Downgrade to INFO and do not relay to monitoring
                                level = AlertLevel.INFO;
                                relayToMonitoring = false;
                            }
                            break;
                        case 2103: // Market data connection lost
                            subject = error.ErrorCodeDescription;
                            if (marketDataProvider?.HandleMarketDataDisconnection() != true)
                            {
                                // Downgrade to INFO and do not relay to monitoring
                                level = AlertLevel.INFO;
                                relayToMonitoring = false;
                            }
                            break;
                        case 2104: // Market data connection resumed
                            subject = error.ErrorCodeDescription;
                            if (marketDataProvider?.HandleMarketDataReconnection() != true)
                            {
                                // Downgrade to INFO and do not relay to monitoring
                                level = AlertLevel.INFO;
                                relayToMonitoring = false;
                            }
                            break;
                        case 2105: // Historical data connection lost
                            subject = error.ErrorCodeDescription;
                            if (marketDataProvider?.HandleHistoricalDataDisconnection() != true)
                            {
                                // Downgrade to INFO and do not relay to monitoring
                                level = AlertLevel.INFO;
                                relayToMonitoring = false;
                            }
                            break;
                        case 2106: // Historical data connection resumed
                            subject = error.ErrorCodeDescription;
                            if (marketDataProvider?.HandleHistoricalDataReconnection() != true)
                            {
                                // Downgrade to INFO and do not relay to monitoring
                                level = AlertLevel.INFO;
                                relayToMonitoring = false;
                            }
                            break;
                        case 10147:
                            subject = $"Order {error.RequestID} to cancel is invalid";
                            body = $"[{error.Level} {error.ErrorCode}] {subject}: {error.ErrorCodeDescription}. Will mark it as cancelled in our systems";
                            orderExecutor.NotifyOrderCancelled(error.RequestID);
                            break;
                        case 10148:
                            subject = $"Order {error.RequestID} cannot be cancelled";
                            orderExecutor.NotifyOrderCancelRequestFailed(error.RequestID, body);
                            break;
                        default:
                            break;
                    }
                    #endregion

                    switch (level)
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

                    if (relayToMonitoring)
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
