﻿using log4net;
using Net.Teirlinck.FX.Data.ContractData;
using Net.Teirlinck.FX.Data.OrderData;
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
        public void RequestPlaceOrder(int orderID, Contract contract, Order order)
        {
            IBApi.Order ibOrder = order.ToIBOrder();
            ClientSocket.placeOrder(orderID, contract.ToIBContract(), ibOrder);
        }

        /// <summary>
        /// Call this method to cancel an order
        /// </summary>
        /// <param name="orderID">The order ID that was specified previously in PlaceOrder()</param>
        public void RequestCancelOrder(int orderID)
        {
            ClientSocket.cancelOrder(orderID);
        }

        /// <summary>
        /// Requests all open orders that were placed from this specific API client (identified by the API client ID). 
        /// Each open order will be fed back through the OrderOpened and OrderStatusReceived events of the responses manager
        /// </summary>
        public void RequestOpenOrdersFromThisClient()
        {
            ClientSocket.reqOpenOrders();
        }

        /// <summary>
        /// Requests all open orders submitted by any API client as well as those directly placed in the TWS
        /// Each open order will be fed back through the OrderOpened and OrderStatusReceived events of the responses manager
        /// </summary>
        public void RequestAllOpenOrders()
        {
            ClientSocket.reqAllOpenOrders();
        }

        /// <summary>
        /// Requests all order placed on the TWS directly. Only the orders created after this request has been made will be returned. 
        /// When a new TWS order is created, the order will be associated with the client
        /// </summary>
        /// <param name="autoBindNewTWSOrdersToThisClient">If set to TRUE, newly created TWS orders will be implicitly associated with the client. If set to FALSE, no association will be made</param>
        public void RequestOpenOrdersFromTWS(bool autoBindNewTWSOrdersToThisClient = false)
        {
            ClientSocket.reqAutoOpenOrders(autoBindNewTWSOrdersToThisClient);
        }

        public async Task<int> GetNextValidOrderID(CancellationToken ct)
        {
            logger.Debug("Requesting next valid order ID");

            _responseManager.NextValidIDReceived += ResponseManager_NextValidIDReceived;

            ClientSocket.reqIds(1); // set to 1 as per IB's documentation

            return await Task.Run(() =>
            {
                try
                {
                    while (_nextValidOrderID < 0)
                    {
                        ct.ThrowIfCancellationRequested();

                        Task.Delay(TimeSpan.FromMilliseconds(500)).Wait();
                    }
                }
                catch (OperationCanceledException)
                {
                    logger.Debug("Cancelling next valid order ID from IB waiting loop");
                }

                _responseManager.NextValidIDReceived -= ResponseManager_NextValidIDReceived;

                int nextValidOrderID = _nextValidOrderID;

                // Reset the _nextValidOrderID member to its original invalid value of -1
                lock (_nextValidOrderIDLocker)
                {
                    _nextValidOrderID = -1;
                }

                return nextValidOrderID;
            }, ct);
        }

        private void ResponseManager_NextValidIDReceived(int nextValidOrderID)
        {
            logger.Debug($"Received next valid order ID: {nextValidOrderID}");

            lock (_nextValidOrderIDLocker)
            {
                this._nextValidOrderID = nextValidOrderID;
            }
        }

        /// <summary>
        /// Call this method to exercise options
        /// </summary>
        /// <param name="requestID">The identifier for the exercise request</param>
        /// <param name="contract">This class contains attributes used to describe the option contract</param>
        /// <param name="exerciseAction">Specifies whether to exercise the specified option or let the option lapse</param>
        /// <param name="exerciseQuantity">The number of contracts to be exercised</param>
        /// <param name="account">For institutional orders. Specifies the destination account</param>
        /// <param name="overrideSystemsAction">Specifies whether your setting will override the system's natural action. 
        /// For example, if your action is "exercise" and the option is not in-the-money, by natural action the option would not exercise. 
        /// If you have override set to "yes" the natural action would be overridden and the out-of-the money option would be exercised</param>
        public void RequestExerciseOption(int requestID, Contract contract, OptionExerciseAction exerciseAction, int exerciseQuantity, string account, bool overrideSystemsAction)
        {
            int exerciseActionInt = OptionExerciseActionUtils.GetIntCode(exerciseAction);
            int overrideSystemsActionInt = overrideSystemsAction ? 1 : 0;

            ClientSocket.exerciseOptions(requestID, contract.ToIBContract(), exerciseActionInt, exerciseQuantity, account, overrideSystemsActionInt);
        }

        /// <summary>
        /// Use this method to cancel all open orders. It cancels orders placed from the API client and orders placed directly in TWS
        /// </summary>
        public void RequestGlobalCancel()
        {
            ClientSocket.reqGlobalCancel();
        }
    }
}
