using log4net;
using Net.Teirlinck.FX.Data.ContractData;
using static Net.Teirlinck.FX.Data.ContractData.Cross;
using static Net.Teirlinck.FX.Data.ContractData.Currency;
using Net.Teirlinck.FX.Data.OrderData;
using static Net.Teirlinck.FX.Data.OrderData.OrderSide;
using static Net.Teirlinck.FX.Data.OrderData.OrderType;
using static Net.Teirlinck.FX.Data.OrderData.OrderStatusCode;
using System;
using System.Collections.Generic;
using System.Linq;
using Capital.GSG.FX.Trading.Executor;
using System.Collections.Concurrent;
using Net.Teirlinck.FX.FXTradingMongoConnector;
using System.Threading.Tasks;
using System.Threading;
using System.Diagnostics;
using Net.Teirlinck.FX.Data.System;
using Capital.GSG.FX.FXConverterServiceConnector;
using Net.Teirlinck.Utils;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI.Executor
{
    public class IBOrderExecutor : IOrderExecutor
    {
        private static ILog logger = LogManager.GetLogger(nameof(IBOrderExecutor));

        private readonly Cross[] nzdPairs = new Cross[] { AUDNZD, EURNZD, GBPNZD, NZDCHF, NZDJPY, NZDUSD };

        private static IBOrderExecutor _instance;

        private readonly CancellationToken stopRequestedCt;

        private readonly BrokerClient brokerClient;
        private readonly ConvertConnector convertServiceConnector;
        private readonly IBClient ibClient;
        private readonly MongoDBServer mongoDBServer;

        private Dictionary<Cross, Contract> contracts = new Dictionary<Cross, Contract>();
        private readonly ConcurrentDictionary<int, Order> orders = new ConcurrentDictionary<int, Order>();

        public event Action<Order> OrderUpdated;

        private int nextValidOrderId = -1;
        private object nextValidOrderIdLocker = new object();
        private bool nextValidOrderIdRequested = false;
        private object nextValidOrderIdRequestedLocker = new object();

        // Order queueus
        private ConcurrentQueue<Order> ordersToPlaceQueue = new ConcurrentQueue<Order>();
        private ConcurrentQueue<int> ordersToCancelQueue = new ConcurrentQueue<int>();

        // Pending requests
        private ConcurrentDictionary<int, Request> orderPlaceRequests = new ConcurrentDictionary<int, Request>();
        private ConcurrentDictionary<int, int> orderCancelRequests = new ConcurrentDictionary<int, int>();

        // Failed orders
        private List<int> rejectedOrders = new List<int>();
        private List<int> rejectedOrderCancellations = new List<int>();

        /// <summary>
        /// Arg 1: requestId
        /// Arg 2: orderId
        /// Arg 3: request status (success / fail)
        /// Arg 4: fill price (if any)
        /// </summary>
        public event Action<int, int, OrderRequestStatus, double?> RequestCompleted;

        private int nextRequestId = 0;
        private object nextRequestIdLocker = new object();

        private IBOrderExecutor(BrokerClient brokerClient, IBClient ibClient, MongoDBServer mongoDBServer, ConvertConnector convertServiceConnector, CancellationToken stopRequestedCt)
        {
            if (brokerClient == null)
                throw new ArgumentNullException(nameof(brokerClient));

            if (ibClient == null)
                throw new ArgumentNullException(nameof(ibClient));

            if (mongoDBServer == null)
                throw new ArgumentNullException(nameof(mongoDBServer));

            if (convertServiceConnector == null)
                throw new ArgumentNullException(nameof(convertServiceConnector));

            this.stopRequestedCt = stopRequestedCt;

            this.brokerClient = brokerClient;

            this.ibClient = ibClient;

            this.ibClient.ResponseManager.OpenOrdersReceived += ResponseManager_OpenOrdersReceived;
            this.ibClient.ResponseManager.OrderStatusChangeReceived += ResponseManager_OrderStatusChangeReceived;

            this.ibClient.IBConnectionEstablished += () =>
            {
                logger.Info("IB client (re)connected. Requesting open orders");
                RequestOpenOrders();
            };

            this.mongoDBServer = mongoDBServer;
            this.convertServiceConnector = convertServiceConnector;
        }

        internal static async Task<IBOrderExecutor> SetupOrderExecutor(BrokerClient brokerClient, IBClient ibClient, MongoDBServer mongoDBServer, ConvertConnector convertServiceConnector, CancellationToken stopRequestedCt)
        {
            _instance = new IBOrderExecutor(brokerClient, ibClient, mongoDBServer, convertServiceConnector, stopRequestedCt);

            await _instance.LoadContracts();

            _instance.StartOrdersPlacingQueue();
            _instance.StartOrdersCancellingQueue();

            _instance.RequestOpenOrders();

            return _instance;
        }

        private void StartOrdersPlacingQueue()
        {
            Task.Run(() =>
            {
                try
                {
                    int loopCounter = 0;

                    while (true)
                    {
                        CancellationTokenSource localCts = null;

                        if (stopRequestedCt.IsCancellationRequested)
                        {
                            // Give it a 5 seconds grace period to drain its queue
                            localCts = new CancellationTokenSource();
                            localCts.CancelAfter(TimeSpan.FromSeconds(5));
                        }

                        while (!ordersToPlaceQueue.IsEmpty)
                        {
                            localCts?.Token.ThrowIfCancellationRequested();

                            Order order;
                            if (ordersToPlaceQueue.TryDequeue(out order))
                            {
                                logger.Debug($"OrdersPlacingQueue loop: dequeuing {order.OrderID}");

                                logger.Info($"Placing order {order.OrderID}: {order}");
                                ibClient.RequestManager.OrdersRequestManager.RequestPlaceOrder(order.OrderID, order.Contract, order);
                            }
                        }

                        if (loopCounter++ % 10 == 0)
                            logger.Debug($"OrdersPlacingQueue loop: {loopCounter}");

                        Task.Delay(TimeSpan.FromSeconds(1)).Wait();
                    }
                }
                catch (OperationCanceledException)
                {
                    logger.Warn("Stopping orders placing queue processing due to stop requested");
                }
            }, stopRequestedCt);
        }

        private void StartOrdersCancellingQueue()
        {
            Task.Run(() =>
            {
                try
                {
                    int loopCounter = 0;

                    while (true)
                    {
                        CancellationTokenSource localCts = null;

                        localCts?.Token.ThrowIfCancellationRequested();

                        if (stopRequestedCt.IsCancellationRequested)
                        {
                            // Give it a 5 seconds grace period to drain its queue
                            localCts = new CancellationTokenSource();
                            localCts.CancelAfter(TimeSpan.FromSeconds(5));
                        }

                        while (!ordersToCancelQueue.IsEmpty)
                        {
                            int orderId;
                            if (ordersToCancelQueue.TryDequeue(out orderId))
                            {
                                logger.Debug($"OrdersPlacingQueue loop: dequeuing {orderId}");

                                ibClient.RequestManager.OrdersRequestManager.RequestCancelOrder(orderId);
                            }
                        }


                        if (loopCounter++ % 10 == 0)
                            logger.Debug($"OrdersCancellingQueue loop: {loopCounter}");

                        Task.Delay(TimeSpan.FromSeconds(1)).Wait();
                    }
                }
                catch (OperationCanceledException)
                {
                    logger.Warn("Stopping orders cancelling queue processing due to stop requested");
                }
            }, stopRequestedCt);
        }

        private int GetNextRequestId()
        {
            lock (nextRequestIdLocker)
            {
                return nextRequestId++;
            }
        }

        private async Task LoadContracts()
        {
            logger.Debug("Loading contracts");

            List<Contract> list = await mongoDBServer.ContractActioner.GetAll(stopRequestedCt);

            if (!list.IsNullOrEmpty())
                contracts = list.Distinct(ContractCrossEqualityComparer.Instance)?.ToDictionary(c => c.Cross, c => c);
            else
                contracts = new Dictionary<Cross, Contract>();

            logger.Info($"Loaded {contracts.Count} contracts");
        }

        private async void ResponseManager_OpenOrdersReceived(int orderId, Contract contract, Order order, OrderState orderState)
        {
            logger.Info($"Received notification of new open order: {orderId}");

            // 1. Enrich
            order.Contract = contract;
            order.Cross = contract?.Cross ?? Cross.UNKNOWN;
            order.WarningMessage = orderState?.WarningMessage;
            order.PlacedTime = DateTime.Now;
            order.LastUpdateTime = DateTime.Now;
            order.EstimatedCommission = EstimateCommission(order.UsdQuantity, order.OrderID);
            order.EstimatedCommissionCcy = order.EstimatedCommission.HasValue ? USD : (Currency?)null;

            #region USD Quantity
            if (CrossUtils.GetQuotedCurrency(order.Cross) == USD)
                order.UsdQuantity = order.Quantity;
            else
            {
                var usdQuantity = await convertServiceConnector.Convert(order.Quantity, CrossUtils.GetQuotedCurrency(order.Cross), USD, stopRequestedCt);
                order.UsdQuantity = usdQuantity.HasValue ? (int)Math.Floor(usdQuantity.Value) : (int?)null;
            }
            #endregion

            if (order.Status == OrderStatusCode.UNKNOWN)
            {
                if (!string.IsNullOrEmpty(orderState?.Status))
                    order.Status = OrderStatusCodeUtils.GetFromStrCode(orderState.Status);
                else
                    order.Status = Submitted;
            }

            // 2. Add or update in the queue
            order.History.Add(new OrderHistoryPoint() { Timestamp = DateTime.Now, Status = order.Status });
            Order updatedOrder = orders.AddOrUpdate(orderId, order, (key, oldValue) => order);

            NotifyOrderUpdate(updatedOrder);
        }

        private void NotifyOrderUpdate(Order order)
        {
            brokerClient.UpdateStatus("OrdersCount", orders.Count, SystemStatusLevel.GREEN);
            OrderUpdated?.Invoke(order);
        }

        private double? EstimateCommission(int? usdQuantity, int orderId)
        {
            if (!usdQuantity.HasValue)
            {
                logger.Error($"Unable to estimate commission costs for order {orderId}: the USD quantity is unknown");
                return null;
            }
            else
            {
                // See https://www.interactivebrokers.com.hk/en/index.php?f=commission&p=fx&ns=T
                double bps = 0.2; // bps
                double min = 2; // USD

                double commission = Math.Max(min, 0.0001 * bps * usdQuantity.Value);

                logger.Debug($"Estimated USD commission for order {orderId} = Math.Max({min}, 0.0001 * {bps} * {usdQuantity.Value}) = {commission}");

                return commission;
            }
        }

        private async Task<double?> CalculateExitProfitabilityLevel(int quantity, Cross cross, OrderSide entrySide, double entryRate, double usdEntryCommission, bool inBps)
        {
            double usdTotalCommission = 2 * usdEntryCommission; // cost of exit = 2 * commission (entry commission + exit commission)

            double? commission = null;

            if (CrossUtils.GetQuotedCurrency(cross) == USD)
                commission = usdTotalCommission;
            else
                commission = await convertServiceConnector.Convert(usdTotalCommission, USD, CrossUtils.GetQuotedCurrency(cross), stopRequestedCt);

            if (!commission.HasValue)
            {
                logger.Error($"Cannot calculate exit profitability level: no exchange rate in DB for {cross}");
                return null;
            }

            double commissionRate = commission.Value / quantity;

            double multiplier = inBps ? 0.0001 : 1.0;

            switch (entrySide)
            {
                case BUY: // must go up to be profitable
                    return (entryRate + commissionRate) * multiplier;
                case SELL: // must go down to be profitable
                    return (entryRate - commissionRate) * multiplier;
                default:
                    return null;
            }
        }

        private void ResponseManager_OrderStatusChangeReceived(int orderId, OrderStatusCode status, int filledQuantity, int remainingQuantity, double avgFillPrice, int permId, int parentId, double lastFillPrice, int clientId, string whyHeld)
        {
            logger.Info($"Received notification of change of status for order {orderId}: status:{status}|filledQuantity:{filledQuantity}|remainingQuantity:{remainingQuantity}|avgFillPrice:{avgFillPrice}|permId:{permId}|parentId:{parentId}|lastFillPrice:{lastFillPrice}|clientId:{clientId}");

            // IB will usually not send an update PreSubmitted => Submitted
            if (status == PreSubmitted)
                status = Submitted;

            Request placeRequest;
            int cancelRequestId;

            // 1. Check if this order was being tracked for placement confirmation
            if (orderPlaceRequests.TryGetValue(orderId, out placeRequest))
            {
                if (status == Submitted || status == PendingSubmit)
                {
                    logger.Debug($"Received submission confirmation for order {orderId}");

                    RequestCompleted?.Invoke(placeRequest.RequestId, orderId, OrderRequestStatus.PendingFill, null);
                }
                else if (status == Filled)
                {
                    logger.Debug($"Received fill confirmation for order {orderId}");

                    RequestCompleted?.Invoke(placeRequest.RequestId, orderId, OrderRequestStatus.Filled, lastFillPrice > 0 ? lastFillPrice : avgFillPrice);

                    orderPlaceRequests.TryRemove(orderId, out placeRequest);
                }
                else
                {
                    NotifyRejectedOrder(orderId, status);

                    PlaceOrder(placeRequest.Order, placeRequest.RequestId);
                }
            }
            // 2. Check if this order was being tracked for cancellation confirmation
            else if (orderCancelRequests.TryGetValue(orderId, out cancelRequestId))
            {
                if (status == PendingCancel)
                {
                    logger.Debug($"Received pending cancellation confirmation for order {orderId}");

                    RequestCompleted?.Invoke(cancelRequestId, orderId, OrderRequestStatus.PendingCancel, null);
                }
                else if (status == Cancelled || status == ApiCanceled)
                {
                    logger.Debug($"Received cancellation confirmation for order {orderId}");

                    RequestCompleted?.Invoke(cancelRequestId, orderId, OrderRequestStatus.Cancelled, null);

                    orderCancelRequests.TryRemove(orderId, out cancelRequestId);
                }
                else
                {
                    NotifyFailedOrderCancellation(orderId, status);

                    CancelOrder(orderId, cancelRequestId);
                }
            }

            // 3. Add or update the order for tracking
            Order order = orders.AddOrUpdate(orderId, (id) =>
            {
                logger.Error($"Received notification of a NEW order ({orderId}). This is unexepected. Please check");

                Order newOrder = new Order()
                {
                    OrderID = orderId,
                    Status = status,
                    PlacedTime = DateTime.Now,
                    LastUpdateTime = DateTime.Now,
                    PermanentID = permId,
                    ParentOrderID = parentId > 0 ? parentId : (int?)null,
                    ClientID = clientId
                };

                return newOrder;
            },
            (key, oldValue) =>
            {
                logger.Debug($"Update status of order {orderId} to {status}");

                DateTime timestamp = DateTime.Now;

                // Add history point (make sure to avoid dupes)
                if (oldValue.History.LastOrDefault<OrderHistoryPoint>()?.Status != status) // the status has actually changed since the last update...
                    oldValue.History.Add(new OrderHistoryPoint() { Status = status, Timestamp = timestamp });

                oldValue.ParentOrderID = parentId > 0 ? parentId : (int?)null;
                oldValue.ClientID = clientId;
                oldValue.PermanentID = permId;

                if (oldValue.Status != Cancelled && oldValue.Status != Filled) // these status or final and can't be changed
                    oldValue.Status = status;

                oldValue.FillPrice = lastFillPrice > 0 ? lastFillPrice : (double?)null;
                oldValue.LastUpdateTime = timestamp;

                // For new bracket orders (the parent) filled we can try to calculate the exit profitability level
                if (status == Filled && parentId < 1 && oldValue.EstimatedCommission.HasValue)
                {
                    Task<double?> task = CalculateExitProfitabilityLevel(oldValue.Quantity, oldValue.Cross, oldValue.Side, lastFillPrice, oldValue.EstimatedCommission.Value, false);

                    task.Wait();

                    oldValue.ExitProfitabilityLevel = task.Result;

                    if (oldValue.ExitProfitabilityLevel.HasValue)
                        logger.Debug($"Calculated an exit profitability level of {oldValue.ExitProfitabilityLevel} for order {oldValue.OrderID} (last fill: {lastFillPrice}");
                    else
                        logger.Debug($"Failed to calculate exit profitability level for order {oldValue.OrderID} (last fill: {lastFillPrice}");
                }

                return oldValue;
            });

            NotifyOrderUpdate(order);
        }

        private void NotifyRejectedOrder(int orderId, OrderStatusCode status)
        {
            rejectedOrders.Add(orderId);

            string err = $"Received rejection notice for order {orderId}. Will attempt to place it again (status was: {status})";

            logger.Error(err);

            brokerClient.UpdateStatus("RejectedOrders", string.Join(",", rejectedOrders), SystemStatusLevel.YELLOW);

            SendError($"Order {orderId} rejected", err);
        }

        private void NotifyFailedOrderCancellation(int orderId, OrderStatusCode status)
        {
            rejectedOrderCancellations.Add(orderId);

            string err = $"Received rejection notice for cancellation of order {orderId}. Will attempt to cancel it again (status was: {status})";

            logger.Error(err);

            brokerClient.UpdateStatus("RejectedOrderCancellations", string.Join(",", rejectedOrderCancellations), SystemStatusLevel.YELLOW);

            SendError($"Order {orderId} cancellation rejected", err);
        }

        private void SendError(string subject, string body)
        {
            brokerClient.OnAlert(new Alert(AlertLevel.ERROR, "IBOrderExecutor", subject, body));
        }

        private Contract GetContract(Cross cross)
        {
            if (contracts.ContainsKey(cross))
                return contracts[cross];
            else
                throw new ArgumentException($"There is no contract information for {cross}");
        }

        private async Task<int> GetNextValidOrderID(CancellationToken stopRequestedCt)
        {
            if (nextValidOrderId < 0)
            {
                CancellationTokenSource internalCts = new CancellationTokenSource();
                internalCts.CancelAfter(TimeSpan.FromSeconds(3)); // Shouldn't take more than 3 seconds for IB to send us the next valid order ID

                if (!nextValidOrderIdRequested)
                {
                    lock (nextValidOrderIdRequestedLocker)
                    {
                        nextValidOrderIdRequested = true;
                    }

                    logger.Debug($"Requesting next valid order ID from IB for first time initialization");

                    Stopwatch sw = new Stopwatch();
                    sw.Start();

                    int nextId = await ibClient.RequestManager.OrdersRequestManager.GetNextValidOrderID(internalCts.Token);

                    sw.Stop();

                    if (nextId > 0)
                    {
                        logger.Debug($"Received next valid order ID from IB after {sw.ElapsedMilliseconds}ms: {nextId}");

                        lock (nextValidOrderIdLocker)
                        {
                            nextValidOrderId = nextId;
                        }
                    }
                    else
                    {
                        logger.Fatal($"Failed to receive the next valid order ID from IB");
                    }
                }
                else
                {
                    logger.Debug("Flag nextValidOrderIdRequested is already set to true but nextValidOrderId is still -1. This suggests that the next valid order ID has been requested from IB but not received yet. Will wait");

                    await Task.Run(() =>
                    {
                        try
                        {
                            while (nextValidOrderId < 0)
                            {
                                // Check the local timeout
                                internalCts.Token.ThrowIfCancellationRequested();

                                // Check the program timeout
                                stopRequestedCt.ThrowIfCancellationRequested();

                                Task.Delay(TimeSpan.FromMilliseconds(500)).Wait();
                            }

                            // It's been received, now we need to increment it
                            lock (nextValidOrderIdLocker)
                            {
                                nextValidOrderId++;
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            logger.Debug("Cancelling next valid order ID waiting loop");
                        }
                    }, internalCts.Token);
                }
            }
            else
            {
                lock (nextValidOrderIdLocker)
                {
                    nextValidOrderId++;
                }

                logger.Debug($"Next valid order ID was increased to {nextValidOrderId}");
            }

            return nextValidOrderId;
        }

        public void Dispose()
        {
            logger.Info("Disposing IBOrderExecutor");

            ibClient.ResponseManager.OrderStatusChangeReceived -= ResponseManager_OrderStatusChangeReceived;
        }

        public int CancelOrder(int orderId, CancellationToken ct = default(CancellationToken))
        {
            return CancelOrder(orderId, null, ct);
        }

        public int CancelOrder(int orderId, int? requestId, CancellationToken ct = default(CancellationToken))
        {
            if (!ibClient.IsConnected())
            {
                logger.Error("Cannot cancel order as the IB client is not connected");

                return -1;
            }

            // If no custom cancellation token is specified we default to the program-level stop requested token
            if (ct == null)
                ct = this.stopRequestedCt;

            if (ct.IsCancellationRequested)
            {
                logger.Error("Not cancelling order: operation cancelled");
                return -1;
            }

            logger.Warn($"Cancelling order {orderId}");

            if (!requestId.HasValue)
                requestId = GetNextRequestId();

            ordersToCancelQueue.Enqueue(orderId);

            orderCancelRequests.AddOrUpdate(orderId, requestId.Value, (key, value) => requestId.Value);

            return requestId.Value;
        }

        public async Task<int> PlaceLimitOrder(Cross cross, OrderSide side, int quantity, double limitPrice, TimeInForce tif, string strategy, int? parentId = null, double? lastBid = null, double? lastMid = null, double? lastAsk = null, CancellationToken ct = default(CancellationToken))
        {
            if (!ibClient.IsConnected())
            {
                logger.Error("Cannot place limit order as the IB client is not connected");

                return -1;
            }

            // If no custom cancellation token is specified we default to the program-level stop requested token
            if (ct == null)
                ct = this.stopRequestedCt;

            if (ct.IsCancellationRequested)
            {
                logger.Error("Not placing order: operation cancelled");
                return -1;
            }

            logger.Info($"Preparing LIMIT order: {side} {quantity} {cross} @ {limitPrice} (TIF: {tif})");

            // 1. Get the next valid ID
            int orderID = await GetNextValidOrderID(ct);

            if (orderID < 0)
            {
                logger.Error("Not placing order: failed to get the next valid order ID");
                return -1;
            }

            // 2. Prepare the limit order
            Order order = new Order();
            order.OrderID = orderID;
            order.Cross = cross;
            order.Side = side;
            order.Quantity = quantity;
            order.Type = LIMIT;
            order.LimitPrice = limitPrice;
            order.TimeInForce = tif;
            order.Contract = GetContract(cross);
            order.OurRef = $"Strategy:{strategy}|Client:{ibClient.ClientName}";
            order.TransmitOrder = true;
            order.ParentOrderID = parentId ?? 0;

            order.LastAsk = lastAsk;
            order.LastBid = lastBid;
            order.LastMid = lastMid;

            // 3. Add the order to the placement queue
            logger.Debug($"Queuing LIMIT order|ID:{order.OrderID}|Cross:{order.Cross}|Side:{order.Side}|Quantity:{order.Quantity}|LimitPrice:{order.LimitPrice}|TimeInForce:{order.TimeInForce}|ParentOrderID:{order.ParentOrderID}");

            return PlaceOrder(order);
        }

        private int PlaceOrder(Order order, int? requestId = null)
        {
            if (!requestId.HasValue)
                requestId = GetNextRequestId();

            ordersToPlaceQueue.Enqueue(order);

            Request request = new Request(requestId.Value, order);

            orderPlaceRequests.AddOrUpdate(order.OrderID, request, (key, value) => request);

            return requestId.Value;
        }

        public async Task<int> PlaceTrailingMarketIfTouchedOrder(Cross cross, OrderSide side, int quantity, double trailingAmount, TimeInForce tif, string strategy, int? parentId = null, double? lastBid = null, double? lastMid = null, double? lastAsk = null, CancellationToken ct = default(CancellationToken))
        {
            if (!ibClient.IsConnected())
            {
                logger.Error("Cannot place TRAILING_MARKET_IF_TOUCHED order as the IB client is not connected");

                return -1;
            }

            // If no custom cancellation token is specified we default to the program-level stop requested token
            if (ct == null)
                ct = this.stopRequestedCt;

            if (ct.IsCancellationRequested)
            {
                logger.Error("Not placing order: operation cancelled");
                return -1;
            }

            logger.Info($"Preparing TRAILING_MARKET_IF_TOUCHED order: {side} {quantity} {cross} @ {trailingAmount} (TIF: {tif})");

            // 1. Get the next valid ID
            int orderID = await GetNextValidOrderID(ct);

            if (orderID < 0)
            {
                logger.Error("Not placing order: failed to get the next valid order ID");
                return -1;
            }

            // 2. Prepare the limit order
            Order order = new Order();
            order.OrderID = orderID;
            order.Cross = cross;
            order.Side = side;
            order.Quantity = quantity;
            order.Type = TRAILING_MARKET_IF_TOUCHED;
            order.TrailingAmount = trailingAmount;
            order.TimeInForce = tif;
            order.Contract = GetContract(cross);
            order.OurRef = $"Strategy:{strategy}|Client:{ibClient.ClientName}";
            order.TransmitOrder = true;
            order.ParentOrderID = parentId ?? 0;

            order.LastAsk = lastAsk;
            order.LastBid = lastBid;
            order.LastMid = lastMid;

            // 3. Add the order to the placement queue
            logger.Debug($"Queuing TRAILING_MARKET_IF_TOUCHED order|ID:{order.OrderID}|Cross:{order.Cross}|Side:{order.Side}|Quantity:{order.Quantity}|TrailingAmount:{order.TrailingAmount}|TimeInForce:{order.TimeInForce}|ParentOrderID:{order.ParentOrderID}");

            return PlaceOrder(order);
        }

        public async Task<int> PlaceStopOrder(Cross cross, OrderSide side, int quantity, double stopPrice, TimeInForce tif, string strategy, int? parentId = null, double? lastBid = null, double? lastMid = null, double? lastAsk = null, CancellationToken ct = default(CancellationToken))
        {
            if (!ibClient.IsConnected())
            {
                logger.Error("Cannot place STOP order as the IB client is not connected");

                return -1;
            }

            // If no custom cancellation token is specified we default to the program-level stop requested token
            if (ct == null)
                ct = this.stopRequestedCt;

            if (ct.IsCancellationRequested)
            {
                logger.Error("Not placing order: operation cancelled");
                return -1;
            }

            logger.Info($"Preparing STOP order: {side} {quantity} {cross} @ {stopPrice} (TIF: {tif})");

            // 1. Get the next valid ID
            int orderID = await GetNextValidOrderID(ct);

            if (orderID < 0)
            {
                logger.Error("Not placing order: failed to get the next valid order ID");
                return -1;
            }

            // 2. Prepare the limit order
            Order order = new Order();
            order.OrderID = orderID;
            order.Cross = cross;
            order.Side = side;
            order.Quantity = quantity;
            order.Type = STOP;
            order.StopPrice = stopPrice;
            order.TimeInForce = tif;
            order.Contract = GetContract(cross);
            order.OurRef = $"Strategy:{strategy}|Client:{ibClient.ClientName}";
            order.TransmitOrder = true;
            order.ParentOrderID = parentId ?? 0;

            order.LastAsk = lastAsk;
            order.LastBid = lastBid;
            order.LastMid = lastMid;

            // 3. Add the order to the placement queue
            logger.Debug($"Queuing STOP order|ID:{order.OrderID}|Cross:{order.Cross}|Side:{order.Side}|Quantity:{order.Quantity}|StopPrice:{order.StopPrice}|TimeInForce:{order.TimeInForce}|ParentOrderID:{order.ParentOrderID}");

            return PlaceOrder(order);
        }

        public async Task<int> PlaceTrailingStopOrder(Cross cross, OrderSide side, int quantity, double trailingAmount, TimeInForce tif, string strategy, int? parentId = null, double? lastBid = null, double? lastMid = null, double? lastAsk = null, CancellationToken ct = default(CancellationToken))
        {
            if (!ibClient.IsConnected())
            {
                logger.Error("Cannot place TRAILING_STOP order as the IB client is not connected");

                return -1;
            }

            // If no custom cancellation token is specified we default to the program-level stop requested token
            if (ct == null)
                ct = this.stopRequestedCt;

            if (ct.IsCancellationRequested)
            {
                logger.Error("Not placing order: operation cancelled");
                return -1;
            }

            logger.Info($"Preparing TRAILING_STOP order: {side} {quantity} {cross} @ {trailingAmount} (TIF: {tif})");

            // 1. Get the next valid ID
            int orderID = await GetNextValidOrderID(ct);

            if (orderID < 0)
            {
                logger.Error("Not placing order: failed to get the next valid order ID");
                return -1;
            }

            // 2. Prepare the limit order
            Order order = new Order();
            order.OrderID = orderID;
            order.Side = side;
            order.Quantity = quantity;
            order.Cross = cross;
            order.Type = TRAILING_STOP;
            order.TrailingAmount = trailingAmount;
            order.TimeInForce = tif;
            order.Contract = GetContract(cross);
            order.OurRef = $"Strategy:{strategy}|Client:{ibClient.ClientName}";
            order.TransmitOrder = true;
            order.ParentOrderID = parentId ?? 0;

            // 3. Add the order to the placement queue
            logger.Debug($"Queuing TRAILING_STOP order|ID:{order.OrderID}|Cross:{order.Cross}|Side:{order.Side}|Quantity:{order.Quantity}|TrailingAmount:{order.TrailingAmount}|TimeInForce:{order.TimeInForce}|ParentOrderID:{order.ParentOrderID}");

            return PlaceOrder(order);
        }

        public async Task<int> PlaceMarketOrder(Cross cross, OrderSide side, int quantity, TimeInForce tif, string strategy, int? parentId = null, double? lastBid = null, double? lastMid = null, double? lastAsk = null, CancellationToken ct = default(CancellationToken))
        {
            if (!ibClient.IsConnected())
            {
                logger.Error("Cannot place MARKET order as the IB client is not connected");

                return -1;
            }

            // If no custom cancellation token is specified we default to the program-level stop requested token
            if (ct == null)
                ct = this.stopRequestedCt;

            if (ct.IsCancellationRequested)
            {
                logger.Error("Not placing order: operation cancelled");
                return -1;
            }

            logger.Info($"Preparing MARKET order: {side} {quantity} {cross} (TIF: {tif})");

            // 1. Get the next valid ID
            int orderID = await GetNextValidOrderID(ct);

            if (orderID < 0)
            {
                logger.Error("Not placing order: failed to get the next valid order ID");
                return -1;
            }

            // 2. Prepare the market order
            Order order = new Order();
            order.OrderID = orderID;
            order.Cross = cross;
            order.Side = side;
            order.Quantity = quantity;
            order.Type = MARKET;
            order.TimeInForce = tif;
            order.Contract = GetContract(cross);
            order.OurRef = $"Strategy:{strategy}|Client:{ibClient.ClientName}";
            order.TransmitOrder = true;
            order.ParentOrderID = parentId ?? 0;

            order.LastAsk = lastAsk;
            order.LastBid = lastBid;
            order.LastMid = lastMid;

            // 3. Add the order to the placement queue
            logger.Debug($"Queuing MARKET order|ID:{order.OrderID}|Cross:{order.Cross}|Side:{order.Side}|Quantity:{order.Quantity}|TimeInForce:{order.TimeInForce}|ParentOrderID:{order.ParentOrderID}");

            return PlaceOrder(order);
        }

        public async Task<bool> CancelAllOrders(IEnumerable<Cross> crosses, CancellationToken ct = default(CancellationToken))
        {
            // If no custom cancellation token is specified we default to the program-level stop requested token
            if (ct == null)
                ct = this.stopRequestedCt;

            if (ct.IsCancellationRequested)
            {
                logger.Error("Not requesting cancel of active orders: operation cancelled");
                return false;
            }

            // Refresh the list of orders
            RequestOpenOrders();

            await Task.Delay(TimeSpan.FromSeconds(1));

            OrderStatusCode[] statusesToCancel = new OrderStatusCode[] { PreSubmitted, Submitted, Inactive };

            var ordersToCancel = from o in orders.Values
                                 where crosses.Contains(o.Cross)
                                 where statusesToCancel.Contains(o.Status)
                                 select o.OrderID;

            if (ordersToCancel.Count() > 0)
            {
                logger.Warn($"Requesting cancel of all active {string.Format(", ", crosses)} orders on IB: orders {string.Format(", ", ordersToCancel)}");

                foreach (int orderId in ordersToCancel)
                {
                    CancelOrder(orderId);
                    await Task.Delay(TimeSpan.FromSeconds(2));
                }
            }

            return true;
        }

        public async Task<bool> CloseAllPositions(IEnumerable<Cross> crosses, CancellationToken ct = default(CancellationToken))
        {
            // If no custom cancellation token is specified we default to the program-level stop requested token
            if (ct == null)
                ct = this.stopRequestedCt;

            if (ct.IsCancellationRequested)
            {
                logger.Error("Not closing positions: operation cancelled");
                return false;
            }

            // 1. Get all open postions
            Dictionary<Cross, double> openPositions = ((IBPositionsExecutor)brokerClient.PositionExecutor).GetAllOpenPositions();

            if (openPositions.IsNullOrEmpty())
            {
                logger.Info("No open position. Nothing to close");
                return true;
            }
            else
            {
                openPositions = openPositions.Where(p => crosses.Contains(p.Key)).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

                if (openPositions.IsNullOrEmpty())
                {
                    logger.Info("No open position. Nothing to close");
                    return true;
                }

                // 2. Place market orders to close all open positions
                bool allClosed = true;

                foreach (var pos in openPositions)
                {
                    logger.Info($"Closing position: {pos.Value} {pos.Key}");

                    OrderSide side = pos.Value > 0 ? SELL : BUY;

                    int orderId = await PlaceMarketOrder(pos.Key, side, Math.Abs((int)Math.Floor(pos.Value)), TimeInForce.DAY, "CloseAllPositions", ct: ct);

                    if (orderId > 0)
                        logger.Debug($"Successfully closed position: {pos.Value} {pos.Key} (order {orderId})");
                    else
                    {
                        logger.Error($"Failed to close position: {pos.Value} {pos.Key}");
                        allClosed = false;
                    }
                }

                return allClosed;
            }
        }

        private void RequestOpenOrders()
        {
            ibClient.RequestManager.OrdersRequestManager.RequestOpenOrdersFromThisClient();
        }

        private class Request
        {
            public int RequestId { get; set; }
            public Order Order { get; set; }

            public Request(int requestId, Order order)
            {
                RequestId = requestId;
                Order = order;
            }
        }

        private class ContractCrossEqualityComparer : IEqualityComparer<Contract>
        {
            private static ContractCrossEqualityComparer instance;

            public static ContractCrossEqualityComparer Instance
            {
                get
                {
                    if (instance == null)
                        instance = new ContractCrossEqualityComparer();

                    return instance;
                }
            }

            private ContractCrossEqualityComparer() { }

            public bool Equals(Contract x, Contract y)
            {
                return x.Cross == y.Cross;
            }

            public int GetHashCode(Contract obj)
            {
                return obj.Cross.GetHashCode();
            }
        }
    }
}
