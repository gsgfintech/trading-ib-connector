using IBApi;
using Net.Teirlinck.FX.InteractiveBrokersAPI.Requests;
using System.Threading;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI
{
    public class IBClientRequestsManager
    {
        private IBClientResponsesManager ResponseManager { get; set; }
        public EClientSocket ClientSocket { get; private set; }

        public MarketDataRequestManager MarketDataRequestManager { get; private set; }
        public OrdersRequestManager OrdersRequestManager { get; private set; }
        public AccountRequestManager AccountRequestManager { get; private set; }
        public ExecutionsRequestManager ExecutionsRequestManager { get; private set; }
        public ContractDetailsRequestManager ContractDetailsRequestManager { get; private set; }
        public MarketDepthRequestManager MarketDepthRequestManager { get; private set; }
        public NewsBulletinRequestManager NewsBulletinRequestManager { get; private set; }
        public FinancialAdvisorsRequestManager FinancialAdvisorsRequestManager { get; private set; }
        public MarketScannerRequestManager MarketScannerRequestManager { get; private set; }
        public HistoricalDataRequestManager HistoricalDataRequestManager { get; private set; }
        public RealTimeBarsRequestManager RealTimeBarsRequestManager { get; private set; }
        public FundamentalDataRequestManager FundamentalDataRequestManager { get; private set; }
        public DisplayGroupsRequestManager DisplayGroupsRequestManager { get; private set; }

        private readonly EReaderMonitorSignal signal = new EReaderMonitorSignal();

        public IBClientRequestsManager(IBClientResponsesManager responseManager = null)
        {
            if (responseManager == null)
                ResponseManager = new IBClientResponsesManager();
            else
                ResponseManager = responseManager;

            ClientSocket = new EClientSocket(ResponseManager, signal);

            MarketDataRequestManager = new MarketDataRequestManager(this);
            OrdersRequestManager = new OrdersRequestManager(this);
            AccountRequestManager = new AccountRequestManager(this);
            ExecutionsRequestManager = new ExecutionsRequestManager(this);
            ContractDetailsRequestManager = new ContractDetailsRequestManager(this);
            MarketDepthRequestManager = new MarketDepthRequestManager(this);
            NewsBulletinRequestManager = new NewsBulletinRequestManager(this);
            FinancialAdvisorsRequestManager = new FinancialAdvisorsRequestManager(this);
            MarketScannerRequestManager = new MarketScannerRequestManager(this);
            HistoricalDataRequestManager = new HistoricalDataRequestManager(this);
            RealTimeBarsRequestManager = new RealTimeBarsRequestManager(this);
            FundamentalDataRequestManager = new FundamentalDataRequestManager(this);
            DisplayGroupsRequestManager = new DisplayGroupsRequestManager(this);
        }

        public IBClientResponsesManager GetResponseManager()
        {
            return ResponseManager;
        }

        /// <summary>
        /// Establishes a connection to TWS/Gateway. After establishing a connection successfully, TWS/Gateway will provide the next valid order id, server's current time, 
        /// managed accounts and open orders among others depending on the TWS/Gateway version
        /// </summary>
        /// <param name="host">The host name or IP address of the machine where TWS is running. Leave blank to connect to the local host</param>
        /// <param name="port">Must match the port specified in TWS on the Configure>API>Socket Port field. 7496 by default for the TWS, 4001 by default on the Gateway</param>
        /// <param name="clientID">Unique ID required of every API client program; can be any integer. Note that up to eight clients can be connected simultaneously to a single instance of TWS or Gateway.
        /// All orders placed/modified from this client will be associated with this client identifier</param>
        public void Connect(int clientID, string host = "", int port = 4001)
        {
            ClientSocket.eConnect(host, port, clientID);

            var reader = new EReader(ClientSocket, signal);

            reader.Start();

            new Thread(() => { while (ClientSocket.IsConnected()) { signal.waitForSignal(); reader.processMsgs(); } }) { IsBackground = true }.Start();
        }

        /// <summary>
        /// Call this method to terminate the connections with TWS. Calling this method does not cancel orders that have already been sent
        /// </summary>
        public void Disconnect()
        {
            ClientSocket.eDisconnect();
        }

        /// <summary>
        /// Call this method to check if there the API client is connected to TWS/Gateway
        /// </summary>
        /// <returns>The connection status</returns>
        public bool IsConnected()
        {
            return ClientSocket.IsConnected();
        }

        /// <summary>
        /// The default level is ERROR
        /// </summary>
        /// <param name="level"></param>
        public void SetServerLogLevel(ServerLogLevel level)
        {
            ClientSocket.setServerLogLevel((int)level);
        }

        /// <summary>
        /// Requests the server's current system time. The currentTime() EWrapper method returns the time
        /// </summary>
        public void RequestCurrentTime()
        {
            ClientSocket.reqCurrentTime();
        }

        /// <summary>
        /// Returns the version of the TWS/Gateway instance to which the API application is connected
        /// </summary>
        /// <returns>The version</returns>
        public int GetServerVersion()
        {
            return ClientSocket.ServerVersion;
        }
    }
}
