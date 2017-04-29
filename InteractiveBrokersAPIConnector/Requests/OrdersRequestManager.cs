using Capital.GSG.FX.Data.Core.ContractData;
using Capital.GSG.FX.Data.Core.FinancialAdvisorsData;
using Capital.GSG.FX.Data.Core.OrderData;
using log4net;
using Net.Teirlinck.FX.InteractiveBrokersAPI.Extensions;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI.Requests
{
    public class OrdersRequestManager
    {
        private static ILog logger = LogManager.GetLogger(nameof(OrdersRequestManager));

        private IBApi.EClientSocket ClientSocket { get; set; }

        private readonly IBClientResponsesManager _responseManager;
        private int _nextValidOrderID = -1;
        private object _nextValidOrderIDLocker = new object();

        private readonly AutoResetEvent waitingForNextValidOrderId = new AutoResetEvent(false);

        public OrdersRequestManager(IBClientRequestsManager requestsManager)
        {
            ClientSocket = requestsManager.ClientSocket;

            this._responseManager = requestsManager.GetResponseManager();
        }

        /// <summary>
        /// Place a new order
        /// </summary>
        /// <param name="orderID">The order's unique identifier. When the order status returns, it will be identified by this ID, which is also used when canceling the order.
        /// Use a sequential ID starting with the ID received at the nextValidId() method</param>
        /// <param name="contract">This class contains attributes used to describe the contract</param>
        /// <param name="order">This structure contains the details of the order</param>
        public void RequestPlaceOrder(int orderID, Contract contract, Order order, string account = null, FAGroup faGroup = null, string faAllocationProfileName = null)
        {
            IBApi.Order ibOrder = order.ToIBOrder();

            if (!string.IsNullOrEmpty(account))
                ibOrder.Account = account;
            else if (faGroup != null)
            {
                ibOrder.FaGroup = faGroup.Name;
                ibOrder.FaMethod = faGroup.DefaultMethod.ToString();

                if (faGroup.DefaultMethod == FAGroupMethod.PctChange)
                    ibOrder.FaPercentage = "100"; // TODO
            }
            else if (!string.IsNullOrEmpty(faAllocationProfileName))
                ibOrder.FaProfile = faAllocationProfileName;

            ClientSocket?.placeOrder(orderID, contract.ToIBContract(), ibOrder);
        }

        /// <summary>
        /// Call this method to cancel an order
        /// </summary>
        /// <param name="orderID">The order ID that was specified previously in PlaceOrder()</param>
        public void RequestCancelOrder(int orderID)
        {
            ClientSocket?.cancelOrder(orderID);
        }

        /// <summary>
        /// Requests all open orders that were placed from this specific API client (identified by the API client ID). 
        /// Each open order will be fed back through the OrderOpened and OrderStatusReceived events of the responses manager
        /// </summary>
        public void RequestOpenOrdersFromThisClient()
        {
            ClientSocket?.reqOpenOrders();
        }

        /// <summary>
        /// Requests all open orders submitted by any API client as well as those directly placed in the TWS
        /// Each open order will be fed back through the OrderOpened and OrderStatusReceived events of the responses manager
        /// </summary>
        public void RequestAllOpenOrders()
        {
            ClientSocket?.reqAllOpenOrders();
        }

        /// <summary>
        /// Requests all order placed on the TWS directly. Only the orders created after this request has been made will be returned. 
        /// When a new TWS order is created, the order will be associated with the client
        /// </summary>
        /// <param name="autoBindNewTWSOrdersToThisClient">If set to TRUE, newly created TWS orders will be implicitly associated with the client. If set to FALSE, no association will be made</param>
        public void RequestOpenOrdersFromTWS(bool autoBindNewTWSOrdersToThisClient = false)
        {
            ClientSocket?.reqAutoOpenOrders(autoBindNewTWSOrdersToThisClient);
        }

        //public async Task<int> GetNextValidOrderID(CancellationToken ct)
        public int GetNextValidOrderID(CancellationToken ct)
        {
            try
            {
                ct.ThrowIfCancellationRequested();

                logger.Debug("Requesting next valid order ID");

                _responseManager.NextValidIDReceived += ResponseManager_NextValidIDReceived;

                ClientSocket?.reqIds(1); // set to 1 as per IB's documentation

                waitingForNextValidOrderId.WaitOne(TimeSpan.FromSeconds(2.5));

                _responseManager.NextValidIDReceived -= ResponseManager_NextValidIDReceived;

                waitingForNextValidOrderId.Reset();
            }
            catch (OperationCanceledException)
            {
                logger.Error("Not requesting next valid order ID: operation cancelled");
            }
            catch (Exception ex)
            {
                logger.Error("Failed to get next valid order ID", ex);
            }

            return _nextValidOrderID;

            //_responseManager.NextValidIDReceived += ResponseManager_NextValidIDReceived;

            //ClientSocket?.reqIds(1); // set to 1 as per IB's documentation

            //return await Task.Run(() =>
            //{
            //    try
            //    {
            //        while (_nextValidOrderID < 0)
            //        {
            //            ct.ThrowIfCancellationRequested();

            //            Task.Delay(TimeSpan.FromMilliseconds(500)).Wait();
            //        }

            //        _responseManager.NextValidIDReceived -= ResponseManager_NextValidIDReceived;

            //        int nextValidOrderID = _nextValidOrderID;

            //        // Reset the _nextValidOrderID member to its original invalid value of -1
            //        lock (_nextValidOrderIDLocker)
            //        {
            //            _nextValidOrderID = -1;
            //        }

            //        return nextValidOrderID;
            //    }
            //    catch (OperationCanceledException)
            //    {
            //        logger.Debug("Cancelling next valid order ID from IB waiting loop");
            //    }
            //    catch (Exception ex)
            //    {
            //        logger.Error("Failed to request next valid order ID", ex);
            //    }

            //    return -1;
            //}, ct);
        }

        private void ResponseManager_NextValidIDReceived(int nextValidOrderID)
        {
            logger.Debug($"Received next valid order ID: {nextValidOrderID}");

            lock (_nextValidOrderIDLocker)
            {
                _nextValidOrderID = nextValidOrderID;
            }

            waitingForNextValidOrderId.Set();
        }

        /// <summary>
        /// Use this method to cancel all open orders. It cancels orders placed from the API client and orders placed directly in TWS
        /// </summary>
        public void RequestGlobalCancel()
        {
            ClientSocket?.reqGlobalCancel();
        }
    }
}
