using Capital.GSG.FX.FXConverterServiceConnector;
using log4net;
using Net.Teirlinck.FX.Data.ContractData;
using static Net.Teirlinck.FX.Data.ContractData.Currency;
using Net.Teirlinck.FX.Data.OrderData;
using static Net.Teirlinck.FX.Data.OrderData.OrderSide;
using static Net.Teirlinck.FX.Data.OrderData.OrderStatusCode;
using static Net.Teirlinck.FX.Data.OrderData.OrderType;
using Net.Teirlinck.FX.FXTradingMongoConnector;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using Net.Teirlinck.Utils;
using Capital.GSG.FX.MarketDataService.Connector;
using Net.Teirlinck.FX.Data.MarketData;
using Net.Teirlinck.FX.Data.System;
using Capital.GSG.FX.Trading.Executor;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI.Executor
{
    public class IBOrderExecutor : IOrderExecutor
    {
        private static ILog logger = LogManager.GetLogger(nameof(IBOrderExecutor));

        private static IBOrderExecutor _instance;

        private readonly CancellationToken stopRequestedCt;

        private readonly BrokerClient brokerClient;
        private readonly ConvertConnector convertServiceConnector;
        private readonly IBClient ibClient;
        private readonly MongoDBServer mongoDBServer;
        private readonly MDConnector mdConnector;
        private readonly ITradingExecutorRunner tradingExecutorRunner;

        private readonly string monitoringEndpoint;

        public event Action<Order> OrderUpdated;

        private Dictionary<Cross, Contract> contracts = new Dictionary<Cross, Contract>();
        private readonly ConcurrentDictionary<int, Order> orders = new ConcurrentDictionary<int, Order>();

        private int nextValidOrderId = -1;
        private object nextValidOrderIdLocker = new object();
        private bool nextValidOrderIdRequested = false;
        private object nextValidOrderIdRequestedLocker = new object();

        // Order queueus
        private ConcurrentQueue<Order> ordersToPlaceQueue = new ConcurrentQueue<Order>();
        private object ordersToPlaceQueueLocker = new object();
        private List<int> ordersPlaced = new List<int>();
        private ConcurrentQueue<int> ordersToCancelQueue = new ConcurrentQueue<int>();
        private List<int> ordersCancelled = new List<int>();
        private ConcurrentDictionary<int, DateTime> ordersAwaitingPlaceConfirmation = new ConcurrentDictionary<int, DateTime>();
        private Timer ordersAwaitingPlaceConfirmationTimer = null;

        internal async Task RequestNextValidOrderID()
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
                    logger.Debug($"Received next valid order ID from IB after {sw.ElapsedMilliseconds}ms: {nextId}. Will increase it to {nextId + 1} to be safe");

                    nextValidOrderId = nextId;
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
                    }
                    catch (OperationCanceledException)
                    {
                        logger.Debug("Cancelling next valid order ID waiting loop");
                    }
                }, internalCts.Token);
            }
        }

        private int GetNextValidOrderID()
        {
            if (nextValidOrderId < 0)
                RequestNextValidOrderID().Wait();

            nextValidOrderId++;
            logger.Debug($"NextValidOrderId was incremented to {nextValidOrderId}");
            return nextValidOrderId;
        }

        private Contract GetContract(Cross cross)
        {
            if (contracts.ContainsKey(cross))
                return contracts[cross];
            else
                throw new ArgumentException($"There is no contract information for {cross}");
        }

        private IBOrderExecutor(BrokerClient brokerClient, IBClient ibClient, MongoDBServer mongoDBServer, ConvertConnector convertServiceConnector, MDConnector mdConnector, ITradingExecutorRunner tradingExecutorRunner, string monitoringEndpoint, CancellationToken stopRequestedCt)
        {
            if (brokerClient == null)
                throw new ArgumentNullException(nameof(brokerClient));

            if (ibClient == null)
                throw new ArgumentNullException(nameof(ibClient));

            if (mongoDBServer == null)
                throw new ArgumentNullException(nameof(mongoDBServer));

            if (convertServiceConnector == null)
                throw new ArgumentNullException(nameof(convertServiceConnector));

            if (mdConnector == null)
                throw new ArgumentNullException(nameof(mdConnector));

            this.stopRequestedCt = stopRequestedCt;

            this.brokerClient = brokerClient;

            this.ibClient = ibClient;

            this.ibClient.ResponseManager.OpenOrdersReceived += ResponseManager_OpenOrdersReceived;
            this.ibClient.ResponseManager.OrderStatusChangeReceived += OnOrderStatusChangeReceived;

            this.ibClient.IBConnectionEstablished += () =>
            {
                logger.Info("IB client (re)connected. Requesting open orders");
                RequestOpenOrders();
            };

            this.mongoDBServer = mongoDBServer;
            this.convertServiceConnector = convertServiceConnector;
            this.mdConnector = mdConnector;
            this.tradingExecutorRunner = tradingExecutorRunner;
            this.monitoringEndpoint = monitoringEndpoint;

            ordersAwaitingPlaceConfirmationTimer = new Timer(OrdersAwaitingPlaceConfirmationCb, null, 5500, 2000);
        }

        internal static async Task<IBOrderExecutor> SetupOrderExecutor(BrokerClient brokerClient, IBClient ibClient, MongoDBServer mongoDBServer, ConvertConnector convertServiceConnector, MDConnector mdConnector, ITradingExecutorRunner tradingExecutorRunner, string monitoringEndpoint, CancellationToken stopRequestedCt)
        {
            _instance = new IBOrderExecutor(brokerClient, ibClient, mongoDBServer, convertServiceConnector, mdConnector, tradingExecutorRunner, monitoringEndpoint, stopRequestedCt);

            await _instance.LoadContracts();

            _instance.StartOrdersPlacingQueue();
            _instance.StartOrdersCancellingQueue();

            _instance.RequestOpenOrders();

            return _instance;
        }

        private void OrdersAwaitingPlaceConfirmationCb(object state)
        {
            Dictionary<int, DateTime> toCheck = ordersAwaitingPlaceConfirmation.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            if (!toCheck.IsNullOrEmpty())
            {
                foreach (var kvp in toCheck)
                {
                    if (DateTime.Now.Subtract(kvp.Value) > TimeSpan.FromSeconds(30))
                    {
                        string err = $"Order {kvp.Key} has been awaiting confirmation for more than 30 seconds ({kvp.Value}). Requesting to cancel it and mark it as such";
                        logger.Error(err);

                        DateTime discarded;
                        if (ordersAwaitingPlaceConfirmation.TryRemove(kvp.Key, out discarded))
                        {
                            SendError($"Unconfirmed order {kvp.Key}", err);
                            CancelOrder(kvp.Key);

                            OnOrderStatusChangeReceived(kvp.Key, ApiCanceled, null, null, null, -1, null, null, 0, "Cancelled because unacked for more than 30 seconds");
                        }
                    }
                }
            }
        }

        private void ResponseManager_OpenOrdersReceived(int orderId, Contract contract, Order order, OrderState orderState)
        {
            logger.Info($"Received notification of open order: {orderId}");

            RemoveOrderFromAwaitingConfirmationList(orderId);

            OrderStatusCode status = OrderStatusCodeUtils.GetFromStrCode(orderState?.Status);

            // IB will usually not send an update PreSubmitted => Submitted
            if (status == PreSubmitted)
                status = Submitted;

            order = orders.AddOrUpdate(orderId, (key) =>
            {
                // Try to load order information from the database
                Task<Order> orderFetchTask = mongoDBServer.OrderActioner.Get(order.PermanentID, stopRequestedCt);
                orderFetchTask.Wait();

                Order existingOrder = (orderFetchTask.IsCompleted && !orderFetchTask.IsCanceled && !orderFetchTask.IsFaulted) ? orderFetchTask.Result : null;

                if (existingOrder == null)
                {
                    logger.Warn($"Order {orderId} was not found in the database. Adding to the list");

                    order.Contract = contract;
                    order.Cross = contract?.Cross ?? Cross.UNKNOWN;
                    order.WarningMessage = orderState?.WarningMessage;
                    order.LastUpdateTime = DateTime.Now;
                    order.Status = status != OrderStatusCode.UNKNOWN ? status : Submitted;
                    order.History.Add(new OrderHistoryPoint() { Timestamp = DateTime.Now, Status = order.Status });

                    return order;
                }
                else
                {
                    logger.Info($"Retrieved information on order {orderId} from the database");

                    existingOrder.WarningMessage = orderState?.WarningMessage;
                    existingOrder.LastUpdateTime = DateTime.Now;

                    if (status != OrderStatusCode.UNKNOWN)
                    {
                        existingOrder.Status = status;

                        if (existingOrder.History.LastOrDefault()?.Status != status)
                            existingOrder.History.Add(new OrderHistoryPoint() { Timestamp = DateTime.Now, Status = status });
                    }

                    return existingOrder;
                }
            }, (key, oldValue) =>
            {
                logger.Info($"Updating order {orderId} in the list");

                order.WarningMessage = orderState?.WarningMessage;
                oldValue.LastUpdateTime = DateTime.Now;

                if (status != OrderStatusCode.UNKNOWN)
                {
                    oldValue.Status = status;

                    if (oldValue.History.LastOrDefault()?.Status != status)
                        oldValue.History.Add(new OrderHistoryPoint() { Timestamp = DateTime.Now, Status = status });
                }

                return oldValue;
            });
        }

        private void RemoveOrderFromAwaitingConfirmationList(int orderId)
        {
            DateTime timestamp;
            if (ordersAwaitingPlaceConfirmation.TryRemove(orderId, out timestamp))
            {
                TimeSpan duration = DateTime.Now.Subtract(timestamp);
                logger.Debug($"Removed order {orderId} from the ordersAwaitingPlaceConfirmation list. Confirmation took {duration.TotalMilliseconds}ms");
            }
        }

        private void SendError(string subject, string body)
        {
            brokerClient.OnAlert(new Alert(AlertLevel.ERROR, nameof(IBOrderExecutor), subject, body));
        }

        private void SendFatal(string subject, string body, string actionUrl = null)
        {
            brokerClient.OnAlert(new Alert(AlertLevel.FATAL, nameof(IBOrderExecutor), subject, body, actionUrl: actionUrl));
        }

        internal void OnOrderStatusChangeReceived(int orderId, OrderStatusCode? status, int? filledQuantity, int? remainingQuantity, double? avgFillPrice, int permId, int? parentId, double? lastFillPrice, int clientId, string whyHeld)
        {
            logger.Info($"Received notification of change of status for order {orderId}: status:{status}|filledQuantity:{filledQuantity}|remainingQuantity:{remainingQuantity}|avgFillPrice:{avgFillPrice}|permId:{permId}|parentId:{parentId}|lastFillPrice:{lastFillPrice}|clientId:{clientId}");

            if (permId == 0)
            {
                string err = $"Order {orderId} has a permanent ID of 0. This is unexpected. Not processing order update";
                logger.Error(err);

                SendError("Order with invalid permanent ID", err);

                return;
            }

            RemoveOrderFromAwaitingConfirmationList(orderId);

            //IB will usually not send an update PreSubmitted => Submitted
            if (status == PreSubmitted)
                status = Submitted;

            // 3. Add or update the order for tracking
            Order order = orders.AddOrUpdate(orderId, (id) =>
            {
                // Try to load order information from the database
                Task<Order> orderFetchTask = mongoDBServer.OrderActioner.Get(permId, stopRequestedCt);
                orderFetchTask.Wait();

                Order existingOrder = (orderFetchTask.IsCompleted && !orderFetchTask.IsCanceled && !orderFetchTask.IsFaulted) ? orderFetchTask.Result : null;

                if (existingOrder == null)
                {
                    logger.Error($"Received update notification of an unknown order ({orderId} / {permId}). This is unexpected. Please check");

                    Order newOrder = new Order();
                    newOrder.OrderID = orderId;
                    newOrder.PermanentID = permId;
                    newOrder.ParentOrderID = parentId;
                    newOrder.ClientID = clientId;
                    newOrder.LastUpdateTime = DateTime.Now;

                    newOrder.Status = status ?? Submitted;

                    if (status == Submitted)
                        newOrder.PlacedTime = DateTime.Now;
                    else if (status == Filled)
                        newOrder.FillPrice = avgFillPrice ?? lastFillPrice;

                    newOrder.WarningMessage = whyHeld;

                    newOrder.History.Add(new OrderHistoryPoint() { Timestamp = DateTime.Now, Status = newOrder.Status });

                    return newOrder;
                }
                else
                {
                    logger.Info($"Retrieved information on order {orderId} from the database");

                    existingOrder.PermanentID = permId;
                    existingOrder.LastUpdateTime = DateTime.Now;

                    if (status.HasValue)
                    {
                        existingOrder.Status = status.Value;

                        if (existingOrder.History.LastOrDefault()?.Status != status.Value)
                            existingOrder.History.Add(new OrderHistoryPoint() { Timestamp = DateTime.Now, Status = existingOrder.Status });
                    }

                    if (status == Filled)
                        existingOrder.FillPrice = avgFillPrice ?? lastFillPrice;

                    existingOrder.WarningMessage = whyHeld;

                    return existingOrder;
                }
            },
            (key, oldValue) =>
            {
                logger.Info($"Received update notification for order {orderId} ({status})");

                if (permId > 0)
                    oldValue.PermanentID = permId;

                if (status.HasValue)
                {
                    oldValue.Status = status.Value;

                    if (oldValue.History.LastOrDefault()?.Status != status.Value)
                        oldValue.History.Add(new OrderHistoryPoint() { Status = status.Value, Timestamp = DateTime.Now });
                }

                if (status == Filled)
                    oldValue.FillPrice = avgFillPrice ?? lastFillPrice;

                oldValue.LastUpdateTime = DateTime.Now;

                return oldValue;
            });

            if (order.PermanentID > 0)
            {
                logger.Info($"Updating order {order.OrderID} ({order.PermanentID}) in database");
                mongoDBServer.OrderActioner.AddOrUpdate(order, stopRequestedCt).Wait();
            }
            else if (order.PermanentID == -1)
                logger.Debug($"Not adding/updating order {order.OrderID} in database: its permanent ID is -1, which means that the order was marked as cancelled after failing at IB");
            else
            {
                string err = $"Unable to add/update order {order.OrderID} in database: the permanent ID is invalid ({order.PermanentID})";
                logger.Error(err);
                SendError($"Unable to add/update order {order.OrderID}", err);
            }

            brokerClient.UpdateStatus("OrdersCount", orders.Count, SystemStatusLevel.GREEN);
            OrderUpdated?.Invoke(order);
        }

        internal void StopTradingStrategyForOrder(int orderId, string message)
        {
            Order order;
            if (orders.TryGetValue(orderId, out order))
            {
                if (order.Strategy != null && !string.IsNullOrEmpty(order.Strategy.Name) && !string.IsNullOrEmpty(order.Strategy.Version))
                {
                    string err = $"Requesting strategy {order.Strategy.Name} {order.Strategy.Version} to stop trading: {message}";

                    logger.Error(err);

                    string actionUrl = order.PermanentID > 0 && !string.IsNullOrEmpty(monitoringEndpoint) ? $"{monitoringEndpoint}/#/orders/id/{order.PermanentID}" : null;

                    SendFatal($"Stop trading for {order.Strategy}", err, actionUrl);

                    tradingExecutorRunner?.StopTradingStrategy(order.Strategy.Name, order.Strategy.Version, message);
                }
                else
                    logger.Error($"Failed to request strategy to stop trading for order {orderId}: strategy is null or invalid");
            }
            else
            {
                logger.Error($"Failed to request strategy to stop trading for order {orderId}: unknown order");
            }
        }

        private void RequestOpenOrders()
        {
            ibClient.RequestManager.OrdersRequestManager.RequestOpenOrdersFromThisClient();
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
                            localCts.CancelAfter(TimeSpan.FromSeconds(10));
                        }

                        while (!ordersToPlaceQueue.IsEmpty)
                        {
                            localCts?.Token.ThrowIfCancellationRequested();

                            Order order;
                            if (ordersToPlaceQueue.TryDequeue(out order))
                            {
                                if (!ordersPlaced.Contains(order.OrderID))
                                {
                                    logger.Debug($"OrdersPlacingQueue loop: dequeuing {order.OrderID}");

                                    logger.Info($"Placing order {order.OrderID}: {order} (contract is null: {order.Contract == null})");
                                    ibClient.RequestManager.OrdersRequestManager.RequestPlaceOrder(order.OrderID, order.Contract, order);

                                    logger.Debug($"Adding order {order.OrderID} to the ordersAwaitingPlaceConfirmation queue");
                                    ordersAwaitingPlaceConfirmation.TryAdd(order.OrderID, DateTime.Now);

                                    ordersPlaced.Add(order.OrderID);

                                    // Random delay in between orders
                                    Thread.Sleep((new Random()).Next(500, 1500));
                                }
                                else
                                    logger.Error($"Not trying to place order {order.OrderID} again");
                            }
                        }

                        if (loopCounter++ % 10 == 0)
                            logger.Debug($"OrdersPlacingQueue loop: {loopCounter}");

                        Thread.Sleep(TimeSpan.FromSeconds(1));
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
                                if (!ordersCancelled.Contains(orderId))
                                {
                                    logger.Debug($"OrdersCancellingQueue loop: dequeuing {orderId}");

                                    ibClient.RequestManager.OrdersRequestManager.RequestCancelOrder(orderId);

                                    ordersCancelled.Add(orderId);

                                    Task.Delay(TimeSpan.FromSeconds(1)).Wait();
                                }
                                else
                                    logger.Error($"Not cancelling order {orderId} again");
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

        private async Task<int?> GetUSDQuantity(Cross cross, int quantity)
        {
            if (CrossUtils.GetQuotedCurrency(cross) == USD)
                return quantity;
            else
            {
                var usdQuantity = await convertServiceConnector.Convert(quantity, CrossUtils.GetQuotedCurrency(cross), USD, stopRequestedCt);
                return usdQuantity.HasValue ? (int)Math.Floor(usdQuantity.Value) : (int?)null;
            }
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

        private async Task<Order> CreateOrder(Cross cross, OrderSide side, int quantity, TimeInForce tif, string strategyName, string strategyVersion, int? parentId, OrderOrigin origin)
        {
            RTBar latest = await mdConnector.GetLatest(cross, stopRequestedCt);

            Order order = new Order()
            {
                Cross = cross,
                Side = side,
                Quantity = quantity,
                TimeInForce = tif,
                Contract = GetContract(cross),
                OurRef = $"Strategy:{strategyName}-{strategyVersion}|Client:{ibClient.ClientName}",
                ParentOrderID = parentId ?? 0,
                LastAsk = latest?.Ask.Close,
                LastBid = latest?.Bid.Close,
                LastMid = latest?.Mid.Close,
                Origin = origin,
                TransmitOrder = true,
                Status = PreSubmitted,
                History = new List<OrderHistoryPoint>() {
                    new OrderHistoryPoint()
                    {
                        Status = PreSubmitted,
                        Timestamp = DateTime.Now
                    }
                },
                LastUpdateTime = DateTime.Now,
                PlacedTime = DateTime.Now,
                UsdQuantity = await GetUSDQuantity(cross, quantity),
                Strategy = new OrderStrategy() { Name = strategyName, Version = strategyVersion }
            };

            #region Commission
            if (order.UsdQuantity.HasValue)
            {
                // See https://www.interactivebrokers.com.hk/en/index.php?f=commission&p=fx&ns=T
                double bps = 0.2; // bps
                double min = 2; // USD

                double commission = Math.Max(min, CrossUtils.GetPipValue(cross) * bps * order.UsdQuantity.Value);

                logger.Debug($"Estimated USD commission for order = Math.Max({min}, {CrossUtils.GetPipValue(cross)} * {bps} * {order.UsdQuantity}) = {commission}");

                order.EstimatedCommission = commission;
                order.EstimatedCommissionCcy = USD;
            }
            #endregion

            return order;
        }

        public async Task<int> PlaceLimitOrder(Cross cross, OrderSide side, int quantity, double limitPrice, TimeInForce tif, string strategyName, string strategyVersion, int? parentId = null, OrderOrigin origin = OrderOrigin.Unknown, CancellationToken ct = default(CancellationToken))
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

            // 1. Prepare the limit order
            Order order = await CreateOrder(cross, side, quantity, tif, strategyName, strategyVersion, parentId, origin);
            order.Type = LIMIT;
            order.LimitPrice = limitPrice;

            // 2. Add the order to the placement queue
            logger.Debug($"Queuing LIMIT order|Side:{order.Side}|Quantity:{order.Quantity}|LimitPrice:{order.LimitPrice}|TimeInForce:{order.TimeInForce}|ParentOrderID:{order.ParentOrderID}");

            return PlaceOrder(order);
        }

        public async Task<int> PlaceStopOrder(Cross cross, OrderSide side, int quantity, double stopPrice, TimeInForce tif, string strategyName, string strategyVersion, int? parentId = null, OrderOrigin origin = OrderOrigin.Unknown, CancellationToken ct = default(CancellationToken))
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

            // 1. Prepare the limit order
            Order order = await CreateOrder(cross, side, quantity, tif, strategyName, strategyVersion, parentId, origin);
            order.Type = STOP;
            order.StopPrice = stopPrice;

            // 2. Add the order to the placement queue
            logger.Debug($"Queuing STOP order|Cross:{order.Cross}|Side:{order.Side}|Quantity:{order.Quantity}|StopPrice:{order.StopPrice}|TimeInForce:{order.TimeInForce}|ParentOrderID:{order.ParentOrderID}");

            return PlaceOrder(order);
        }

        public async Task<int> PlaceMarketOrder(Cross cross, OrderSide side, int quantity, TimeInForce tif, string strategyName, string strategyVersion, int? parentId = null, OrderOrigin origin = OrderOrigin.Unknown, CancellationToken ct = default(CancellationToken))
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

            // 1. Prepare the market order
            Order order = await CreateOrder(cross, side, quantity, tif, strategyName, strategyVersion, parentId, origin);
            order.Type = MARKET;

            // 2. Add the order to the placement queue
            logger.Debug($"Queuing MARKET order|Cross:{order.Cross}|Side:{order.Side}|Quantity:{order.Quantity}|TimeInForce:{order.TimeInForce}|ParentOrderID:{order.ParentOrderID}");

            return PlaceOrder(order);
        }

        public async Task<int> PlaceTrailingMarketIfTouchedOrder(Cross cross, OrderSide side, int quantity, double trailingAmount, TimeInForce tif, string strategyName, string strategyVersion, int? parentId = null, OrderOrigin origin = OrderOrigin.Unknown, CancellationToken ct = default(CancellationToken))
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

            // 1. Prepare the order
            Order order = await CreateOrder(cross, side, quantity, tif, strategyName, strategyVersion, parentId, origin);
            order.Type = TRAILING_MARKET_IF_TOUCHED;
            order.TrailingAmount = trailingAmount;

            // 2. Add the order to the placement queue
            logger.Debug($"Queuing TRAILING_MARKET_IF_TOUCHED order|Cross:{order.Cross}|Side:{order.Side}|Quantity:{order.Quantity}|TrailingAmount:{order.TrailingAmount}|TimeInForce:{order.TimeInForce}|ParentOrderID:{order.ParentOrderID}");

            return PlaceOrder(order);
        }

        public async Task<int> PlaceTrailingStopOrder(Cross cross, OrderSide side, int quantity, double trailingAmount, TimeInForce tif, string strategyName, string strategyVersion, int? parentId = null, OrderOrigin origin = OrderOrigin.Unknown, CancellationToken ct = default(CancellationToken))
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

            // 1. Prepare the order
            Order order = await CreateOrder(cross, side, quantity, tif, strategyName, strategyVersion, parentId, origin);
            order.Type = TRAILING_STOP;
            order.TrailingAmount = trailingAmount;

            // 2. Add the order to the placement queue
            logger.Debug($"Queuing TRAILING_STOP order|ID:{order.OrderID}|Cross:{order.Cross}|Side:{order.Side}|Quantity:{order.Quantity}|TrailingAmount:{order.TrailingAmount}|TimeInForce:{order.TimeInForce}|ParentOrderID:{order.ParentOrderID}");

            return PlaceOrder(order);
        }

        private int PlaceOrder(Order order)
        {
            lock (ordersToPlaceQueueLocker)
            {
                int orderID = GetNextValidOrderID();

                if (orderID < 0)
                {
                    logger.Error("Not placing order: failed to get the next valid order ID");
                    return -1;
                }

                order.OrderID = orderID;

                logger.Info($"Adding order {order.OrderID} to the list");
                orders.TryAdd(order.OrderID, order);

                logger.Info($"Adding order {order.OrderID} to the ordersToPlace queue");
                ordersToPlaceQueue.Enqueue(order);

                return orderID;
            }
        }

        public bool CancelOrder(int orderId, CancellationToken ct = default(CancellationToken))
        {
            if (!ibClient.IsConnected())
            {
                logger.Error("Cannot cancel order as the IB client is not connected");

                return false;
            }

            // If no custom cancellation token is specified we default to the program-level stop requested token
            if (ct == null)
                ct = this.stopRequestedCt;

            if (ct.IsCancellationRequested)
            {
                logger.Error("Not cancelling order: operation cancelled");
                return false;
            }

            logger.Warn($"Cancelling order {orderId}");

            ordersToCancelQueue.Enqueue(orderId);

            return true;
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
                logger.Warn($"Requesting cancel of all active {string.Join(", ", crosses)} orders on IB: orders {string.Join(", ", ordersToCancel)}");

                foreach (int orderId in ordersToCancel)
                {
                    CancelOrder(orderId);
                    await Task.Delay(TimeSpan.FromSeconds(2));
                }
            }

            return true;
        }

        public async Task<bool> CloseAllPositions(IEnumerable<Cross> crosses, OrderOrigin origin = OrderOrigin.PositionClose_TE, CancellationToken ct = default(CancellationToken))
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

                    int orderId = await PlaceMarketOrder(pos.Key, side, Math.Abs((int)Math.Floor(pos.Value)), TimeInForce.DAY, "CloseAllPositions", "1.0", origin: origin, ct: ct);

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

        public async Task<int> UpdateOrderLevel(int orderId, double newLevel, CancellationToken ct = default(CancellationToken))
        {
            logger.Info($"Replacing order {orderId} with a new order at level {newLevel}");

            Order currentOrder;
            if (!orders.TryGetValue(orderId, out currentOrder))
            {
                logger.Error($"Order {orderId} is unknown. Unable to update its level");
                return -1;
            }

            switch (currentOrder.Type)
            {
                case LIMIT:
                    // 1. Place new order
                    int newLimitOrderId = await PlaceLimitOrder(currentOrder.Cross, currentOrder.Side, currentOrder.Quantity, newLevel, currentOrder.TimeInForce, currentOrder.Strategy?.Name, currentOrder.Strategy?.Version, currentOrder.ParentOrderID, currentOrder.Origin, ct);

                    if (newLimitOrderId > -1)
                    {
                        // 2. Cancel original order
                        if (CancelOrder(orderId, ct))
                            logger.Info($"Successfully cancelled order {orderId}");
                        else
                            logger.Error($"Failed to cancel order {orderId}");
                    }
                    else
                        logger.Error($"Failed to place new limit order. Not cancelling {orderId}");

                    return newLimitOrderId;
                case STOP:
                    // 1. Place new order
                    int newStopOrderId = await PlaceStopOrder(currentOrder.Cross, currentOrder.Side, currentOrder.Quantity, newLevel, currentOrder.TimeInForce, currentOrder.Strategy?.Name, currentOrder.Strategy?.Version, currentOrder.ParentOrderID, currentOrder.Origin, ct);

                    if (newStopOrderId > -1)
                    {
                        // 2. Cancel original order
                        if (CancelOrder(orderId, ct))
                            logger.Info($"Successfully cancelled order {orderId}");
                        else
                            logger.Error($"Failed to cancel order {orderId}");
                    }
                    else
                        logger.Error($"Failed to place new stop order. Not cancelling {orderId}");

                    return newStopOrderId;
                default:
                    logger.Error($"Updating the level of orders of type {currentOrder.Type} is not supported");
                    return -1;
            }
        }

        internal Order GetOrder(int orderId)
        {
            Order order;

            if (orders.TryGetValue(orderId, out order))
                return order;
            else
            {
                logger.Error($"Unable to find order {orderId} in the list");
                return null;
            }
        }

        public void Dispose()
        {
            logger.Info("Disposing IBOrderExecutor");

            try { ordersAwaitingPlaceConfirmationTimer?.Dispose(); ordersAwaitingPlaceConfirmationTimer = null; } catch { }

            ibClient.ResponseManager.OrderStatusChangeReceived -= OnOrderStatusChangeReceived;
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
