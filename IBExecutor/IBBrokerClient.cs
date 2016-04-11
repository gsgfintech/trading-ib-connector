using Capital.GSG.FX.Trading.Executor;
using System;
using System.Threading.Tasks;
using Net.Teirlinck.FX.Data;
using System.Threading;
using log4net;
using Net.Teirlinck.FX.FXTradingMongoConnector;
using System.Collections.Generic;
using Net.Teirlinck.FX.Data.System;
using static Net.Teirlinck.FX.Data.System.SystemStatusLevel;
using System.Linq;
using Net.Teirlinck.Utils;
using Capital.GSG.FX.FXConverterServiceConnector;
using Capital.GSG.FX.MarketDataService.Connector;

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
        private const string IsConnectedKey = "IsConnected";
        private const string MessageKey = "Message";
        private const string StatusKey = "Status";

        private static BrokerClient _instance;

        private readonly IBClient ibClient;
        private readonly string clientName;

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

        private SystemStatus Status { get; set; }
        public event Action<SystemStatus> StatusUpdated;

        public event Action<Alert> AlertReceived;

        public event Action StopComplete;

        Timer statusUpdateTimer = null;

        private BrokerClient(IBrokerClientType clientType, ITradingExecutorRunner tradingExecutorRunner, int clientID, string clientName, string socketHost, int socketPort, IEnumerable<APIErrorCode> ibApiErrorCodes, CancellationToken stopRequestedCt)
        {
            this.brokerClientType = clientType;
            this.clientName = clientName;
            this.tradingExecutorRunner = tradingExecutorRunner;

            Status = new SystemStatus(clientName);

            ibClient = new IBClient(clientID, clientName, socketHost, socketPort, ibApiErrorCodes, stopRequestedCt);
            ibClient.APIErrorReceived += IbClient_APIErrorReceived;
            ibClient.IBConnectionEstablished += () => UpdateStatus(IsConnectedKey, true, GREEN);
            ibClient.IBConnectionLost += () => UpdateStatus(IsConnectedKey, false, RED);

            // Throttle status updates to one every five seconds
            // Delay the first status update by 3 seconds, otherwise the client is already connected before the trade engine has had time to wire up the "StatusUpdated" event listener
            statusUpdateTimer = new Timer(state => SendStatusUpdate(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5));
        }

        public static async Task<IBrokerClient> SetupBrokerClient(IBrokerClientType clientType, ITradingExecutorRunner tradingExecutorRunner, Dictionary<string, object> clientConfig, MongoDBServer mongoDBServer, ConvertConnector convertServiceConnector, MDConnector mdConnector, CancellationToken stopRequestedCt, bool logTicks)
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

            if (mongoDBServer == null)
                throw new ArgumentNullException(nameof(mongoDBServer));

            if (convertServiceConnector == null)
                throw new ArgumentNullException(nameof(convertServiceConnector));

            logger.Info($"Loading IB client config for {name}");
            logger.Info($"ClientNumber: {number}");
            logger.Info($"IBHost: {host}");
            logger.Info($"IBPort: {port}");
            logger.Info($"TradingAccount: {tradingAccount}");

            List<APIErrorCode> ibApiErrorCodes = await mongoDBServer.APIErrorCodeActioner.GetAll(stopRequestedCt);

            _instance = new BrokerClient(clientType, tradingExecutorRunner, number, name, host, port, ibApiErrorCodes, stopRequestedCt);

            logger.Info("Setup broker client complete. Wait for 2 seconds before setting up executors");
            Task.Delay(TimeSpan.FromSeconds(2)).Wait();

            await _instance.SetupExecutors(mongoDBServer, convertServiceConnector, mdConnector, tradingAccount, logTicks, stopRequestedCt);

            return _instance;
        }

        private async Task SetupExecutors(MongoDBServer mongoDBServer, ConvertConnector convertServiceConnector, MDConnector mdConnector, string tradingAccount, bool logTicks, CancellationToken stopRequestedCt)
        {
            if (brokerClientType != IBrokerClientType.MarketData)
            {
                logger.Info("Setting up orders executor, positions executor and trades executor");

                orderExecutor = await IBOrderExecutor.SetupOrderExecutor(this, ibClient, mongoDBServer, convertServiceConnector, mdConnector, stopRequestedCt);
                positionExecutor = IBPositionsExecutor.SetupIBPositionsExecutor(ibClient, tradingAccount, convertServiceConnector, stopRequestedCt);
                tradesExecutor = new IBTradesExecutor(this, ibClient, convertServiceConnector, stopRequestedCt);
            }

            if (brokerClientType != IBrokerClientType.Trading)
            {
                logger.Info("Setting up market data provider and news bulletins provider");

                marketDataProvider = await IBMarketDataProvider.SetupIBMarketDataProvider(this, ibClient, mongoDBServer, logTicks, stopRequestedCt);
                newsBulletinProvider = new IBNewsBulletinProvider(ibClient);
            }
        }

        private async void IbClient_APIErrorReceived(APIError error)
        {
            if (error != null)
            {
                if (error.ErrorMessage.Contains(" data farm connection is"))
                {
                    if (error.ErrorMessage.Contains(" broken:"))
                        AlertReceived?.Invoke(new Alert(AlertLevel.ERROR, clientName, "Broken IB Market Data", error.ErrorMessage));
                    else if (error.ErrorMessage.Contains(" OK:"))
                        AlertReceived?.Invoke(new Alert(AlertLevel.INFO, clientName, "Resumed IB Market Data", error.ErrorMessage));
                }
                else if (error.ErrorMessage.Contains("Historical data request pacing violation"))
                    AlertReceived?.Invoke(new Alert(AlertLevel.ERROR, clientName, "Max rate of msg/second exceeded", error.ErrorMessage));
                else if (error.ErrorCode != null)
                {
                    logger.Error($"Received error message from IB: [{error.ErrorCode.Level}] {error.ErrorMessage} (code: {error.ErrorCode.Code} - {error.ErrorCode.Description})");

                    string subject = error.ErrorCode.Description;
                    AlertLevel level = AlertLevel.ERROR;
                    bool send = true;

                    switch (error.ErrorCode.Code)
                    {
                        // Specific handlers
                        case 100:
                            subject = "Max rate of msg/second exceeded";
                            break;
                        case 103:
                            subject = "Duplicate Order ID";
                            level = AlertLevel.WARNING;
                            logger.Info("Received duplicate order ID error. Will notify order executor to increment its next valid order ID");
                            await orderExecutor.RequestNextValidOrderID();
                            break;
                        case 110:
                            subject = "Order Rejected";
                            break;
                        case 2100:
                            subject = "Account Management";
                            level = AlertLevel.INFO;
                            break;
                        // Generic handler, for errors only
                        default:
                            send = error.ErrorCode.Level == APIErrorCodeLevel.ERROR;
                            break;
                    }

                    if (send)
                        AlertReceived?.Invoke(new Alert(level, clientName, $"{subject} (request {error.RequestID})", error.ToString()));
                }
                else
                    logger.Error($"Received unclassified error message from IB: {error.ErrorMessage}");
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

            UpdateStatus(MessageKey, "Stop complete", RED);

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
            if (string.IsNullOrEmpty(attributeName))
                return;

            var attribute = Status.Attributes.Where(attr => attr.Name == attributeName).FirstOrDefault();

            if (attribute == null)
                Status.Attributes.Add(new SystemStatusAttribute(attributeName, attributeValue, attributeLevel));
            else
            {
                attribute.Value = attributeValue;
                attribute.Level = attributeLevel;
            }
        }

        /// <summary>
        /// Updates the status object but does not send it. Status will be throttled and sent by statusUpdateTimer or by calling SendStatusUpdate directly
        /// </summary>
        /// <param name="attributes">Param 1: attribute key, Param 2: attribute value, Param 3: attribute status level</param>
        internal void UpdateStatus(IEnumerable<SystemStatusAttribute> attributes)
        {
            if (CollectionUtils.IsNullOrEmpty(attributes))
                return;

            foreach (var attribute in attributes)
                UpdateStatus(attribute.Name, attribute.Value, attribute.Level);
        }

        private void SendStatusUpdate()
        {
            StatusUpdated?.Invoke(Status);
        }
    }
}
