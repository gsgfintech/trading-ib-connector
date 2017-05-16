using log4net;
using Capital.GSG.FX.Data.Core.ContractData;
using static Capital.GSG.FX.Data.Core.ContractData.Currency;
using static Capital.GSG.FX.Data.Core.OrderData.OrderSide;
using static Capital.GSG.FX.Data.Core.OrderData.OrderStatusCode;
using static Capital.GSG.FX.Data.Core.OrderData.OrderType;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using Capital.GSG.FX.Data.Core.SystemData;
using Capital.GSG.FX.FXConverter;
using Capital.GSG.FX.Data.Core.OrderData;
using Capital.GSG.FX.Trading.Executor.Core;
using Capital.GSG.FX.Utils.Core;
using Capital.GSG.FX.Data.Core.WebApi;
using Capital.GSG.FX.Data.Core.FinancialAdvisorsData;
using Capital.GSG.FX.Data.Core.AccountPortfolio;
using Newtonsoft.Json;
using MarketDataService.Connector;
using Capital.GSG.FX.Data.Core.MarketData;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI.Executor
{
    public class IBOrderExecutor : IOrderExecutor
    {
        private static ILog logger = LogManager.GetLogger(nameof(IBOrderExecutor));

        private static IBOrderExecutor _instance;

        private readonly CancellationToken stopRequestedCt;

        private readonly BrokerClient brokerClient;
        private readonly IFxConverter fxConverter;
        private readonly IBClient ibClient;
        private readonly MDConnector mdConnector;
        private readonly ITradingExecutorRunner tradingExecutorRunner;

        private readonly string monitoringEndpoint;

        public event Action<Order> OrderUpdated;

        private Dictionary<Cross, Contract> contracts = new Dictionary<Cross, Contract>();
        private readonly ConcurrentDictionary<int, Order> orders = new ConcurrentDictionary<int, Order>();
        private readonly ConcurrentDictionary<int, Order> openVirtualOrders = new ConcurrentDictionary<int, Order>();

        private int nextValidOrderId = -1;
        private object nextValidOrderIdLocker = new object();
        private bool nextValidOrderIdRequested = false;
        private object nextValidOrderIdRequestedLocker = new object();

        // Order queues
        private ConcurrentQueue<OrderToPlace> ordersToPlaceQueue = new ConcurrentQueue<OrderToPlace>();
        private object ordersToPlaceQueueLocker = new object();
        private List<int> ordersPlaced = new List<int>();
        private ConcurrentQueue<int> ordersToCancelQueue = new ConcurrentQueue<int>();
        private ConcurrentDictionary<int, DateTimeOffset> ordersCancelled = new ConcurrentDictionary<int, DateTimeOffset>();
        private ConcurrentDictionary<int, DateTimeOffset> ordersAwaitingPlaceConfirmation = new ConcurrentDictionary<int, DateTimeOffset>();
        private Timer ordersAwaitingPlaceConfirmationTimer = null;

        private ConcurrentDictionary<int, DateTimeOffset> ordersAwaitingCancellationConfirmation = new ConcurrentDictionary<int, DateTimeOffset>();

        private ConcurrentDictionary<int, string> failedOrderCancellationRequests = new ConcurrentDictionary<int, string>();

        private bool isTradingConnectionLost = false;
        private object isTradingConnectionLostLocker = new object();
        private DateTimeOffset lastTradingConnectionLostCheck = DateTimeOffset.MinValue;
        private object lastTradingConnectionLostCheckLocker = new object();
        private DateTimeOffset lastTradingConnectionResumedCheck = DateTimeOffset.MinValue;
        private object lastTradingConnectionResumedCheckLocker = new object();

        private bool isInstitutionalAccount;
        private object isInstitutionalAccountLocker = new object();

        public event Action TradingConnectionLost;
        public event Action TradingConnectionResumed;

        private FAConfiguration lastKnownFAConfiguration;

        private const string SingleTicketAllocation = "Single";
        private const string DoubleTicketAllocation = "Double";

        internal void NotifyOrderCancelRequestFailed(int orderId, string error)
        {
            error = error ?? "Unknown error";

            logger.Error($"Failed to cancel order {orderId}: {error}");

            failedOrderCancellationRequests.TryAdd(orderId, error);
        }

        internal void NotifyOrderCancelled(int orderId)
        {
            logger.Info($"Received cancellation notification for order {orderId}");
            HandleOrderCancellation(orderId);
        }

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

                int nextId = ibClient.RequestManager.OrdersRequestManager.GetNextValidOrderID(internalCts.Token);

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

        private IBOrderExecutor(BrokerClient brokerClient, IBClient ibClient, IFxConverter fxConverter, MDConnector mdConnector, ITradingExecutorRunner tradingExecutorRunner, string monitoringEndpoint, CancellationToken stopRequestedCt)
        {
            this.stopRequestedCt = stopRequestedCt;

            this.brokerClient = brokerClient ?? throw new ArgumentNullException(nameof(brokerClient));

            this.ibClient = ibClient ?? throw new ArgumentNullException(nameof(ibClient));

            this.ibClient.ResponseManager.OpenOrdersReceived += OnOpenOrdersReceived;
            this.ibClient.ResponseManager.OrderStatusChangeReceived += OnOrderStatusChangeReceived;

            this.ibClient.IBConnectionEstablished += () =>
            {
                SetIsTradingConnectionLostFlag(false);
                TradingConnectionResumed?.Invoke();

                logger.Info("IB client (re)connected. Requesting open orders");
                RequestOpenOrders();
            };

            this.fxConverter = fxConverter ?? throw new ArgumentNullException(nameof(fxConverter));
            this.mdConnector = mdConnector ?? throw new ArgumentNullException(nameof(mdConnector));
            this.tradingExecutorRunner = tradingExecutorRunner;
            this.monitoringEndpoint = monitoringEndpoint;

            ordersAwaitingPlaceConfirmationTimer = new Timer(OrdersAwaitingPlaceConfirmationCb, null, 5500, 2000);
        }

        internal static IBOrderExecutor SetupOrderExecutor(BrokerClient brokerClient, IBClient ibClient, IFxConverter fxConverter, MDConnector mdConnector, ITradingExecutorRunner tradingExecutorRunner, string monitoringEndpoint, IEnumerable<Contract> ibContracts, CancellationToken stopRequestedCt)
        {
            _instance = new IBOrderExecutor(brokerClient, ibClient, fxConverter, mdConnector, tradingExecutorRunner, monitoringEndpoint, stopRequestedCt);

            _instance.LoadContracts(ibContracts);

            _instance.StartOrdersPlacingQueue();
            _instance.StartOrdersCancellingQueue();

            _instance.RequestOpenOrders();

            return _instance;
        }

        private async void OrdersAwaitingPlaceConfirmationCb(object state)
        {
            Dictionary<int, DateTimeOffset> toCheck = ordersAwaitingPlaceConfirmation.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            if (!toCheck.IsNullOrEmpty())
            {
                foreach (var kvp in toCheck)
                {
                    if (DateTimeOffset.Now.Subtract(kvp.Value) > TimeSpan.FromMinutes(3))
                    {
                        string err = $"Order {kvp.Key} has been awaiting confirmation for more than 3 minutes ({kvp.Value}). Requesting to cancel it and mark it as such";
                        logger.Error(err);

                        DateTimeOffset discarded;
                        if (ordersAwaitingPlaceConfirmation.TryRemove(kvp.Key, out discarded))
                        {
                            SendError($"Unconfirmed order {kvp.Key}", err);
                            await CancelOrder(kvp.Key);

                            int tries = 0;

                            while (!IsConfirmedCancelled(kvp.Key).Item1 && tries < 5)
                            {
                                tries++;

                                if (tries == 5)
                                {
                                    string err2 = $"Order {kvp.Key} has still not been confirmed cancelled after 5 seconds";
                                    logger.Error(err2);
                                    SendError($"Order not cancelled {kvp.Key}", err);

                                    OnOrderStatusChangeReceived(kvp.Key, ApiCanceled, null, null, null, -1, null, null);
                                }
                            }
                        }
                    }
                }
            }
        }

        private void OnOpenOrdersReceived(int orderId, Contract contract, Order order, OrderState orderState)
        {
            logger.Info($"Received notification of open order: {orderId} (perm ID: {order.PermanentID})");

            if (order.PermanentID <= 0)
            {
                string err = $"Order {orderId} has a permanent ID of {order.PermanentID}. This is unexpected. Not processing open order notification";
                logger.Error(err);

                SendError("Order with invalid permanent ID", err);

                return;
            }

            RemoveOrderFromAwaitingConfirmationList(orderId);

            OrderStatusCode status = OrderStatusCodeUtils.GetFromStrCode(orderState?.Status);

            // IB will usually not send an update PreSubmitted => Submitted
            if (status == PreSubmitted)
                status = Submitted;

            order = orders.AddOrUpdate(orderId, (key) =>
            {
                logger.Warn($"Received update of new order {orderId}. Adding to the list");

                order.Cross = contract?.Cross ?? Cross.UNKNOWN;
                order.LastUpdateTime = DateTimeOffset.Now;
                order.Status = status != OrderStatusCode.UNKNOWN ? status : Submitted;
                order.History.Add(new OrderHistoryPoint() { Timestamp = DateTimeOffset.Now, Status = order.Status });

                return order;
            }, (key, oldValue) =>
            {
                logger.Info($"Updating order {orderId} in the list");

                oldValue.LastUpdateTime = DateTimeOffset.Now;

                if (status != OrderStatusCode.UNKNOWN)
                {
                    oldValue.Status = status;

                    if (oldValue.History.LastOrDefault()?.Status != status)
                        oldValue.History.Add(new OrderHistoryPoint() { Timestamp = DateTimeOffset.Now, Status = status });
                }

                return oldValue;
            });
        }

        private void RemoveOrderFromAwaitingConfirmationList(int orderId)
        {
            DateTimeOffset timestamp;
            if (ordersAwaitingPlaceConfirmation.TryRemove(orderId, out timestamp))
            {
                TimeSpan duration = DateTimeOffset.Now.Subtract(timestamp);
                logger.Debug($"Removed order {orderId} from the ordersAwaitingPlaceConfirmation list. Confirmation took {duration.TotalMilliseconds}ms");
            }
        }

        private void SendError(string subject, string body)
        {
            brokerClient.OnAlert(new Alert() { Level = AlertLevel.ERROR, Source = nameof(IBOrderExecutor), Subject = subject, Body = body, Timestamp = DateTimeOffset.Now, AlertId = Guid.NewGuid().ToString() });
        }

        private void SendFatal(string subject, string body, string actionUrl = null)
        {
            brokerClient.OnAlert(new Alert() { Level = AlertLevel.FATAL, Source = nameof(IBOrderExecutor), Subject = subject, Body = body, ActionUrl = actionUrl, Timestamp = DateTimeOffset.Now, AlertId = Guid.NewGuid().ToString() });
        }

        internal void OnOrderStatusChangeReceived(int orderId, OrderStatusCode? status, double? filledQuantity, double? remainingQuantity, double? avgFillPrice, long permId, int? parentId, double? lastFillPrice)
        {
            logger.Info($"Received notification of change of status for order {orderId}: status:{status}|filledQuantity:{filledQuantity}|remainingQuantity:{remainingQuantity}|avgFillPrice:{avgFillPrice}|permId:{permId}|parentId:{parentId}|lastFillPrice:{lastFillPrice}");

            if (permId == 0)
            {
                string err = $"Received an update for order {orderId} with permId of 0. This is unexpected. Not processing order update";
                logger.Error(err);

                SendError("Order update with invalid permanent ID", err);

                return;
            }

            RemoveOrderFromAwaitingConfirmationList(orderId);

            if (status == PreSubmitted) // IB will usually not send an update PreSubmitted => Submitted
                status = Submitted;
            else if (status == ApiCanceled || status == Cancelled)
                HandleOrderCancellation(orderId);

            // 3. Add or update the order for tracking
            Order order = orders.AddOrUpdate(orderId, (id) =>
            {
                if (permId > 0)
                {
                    logger.Error($"Received update notification of an unknown order ({orderId} / {permId}). This is unexpected. Please check");

                    Order newOrder = new Order();
                    newOrder.OrderID = orderId;
                    newOrder.PermanentID = permId;
                    newOrder.ParentOrderID = parentId;
                    newOrder.LastUpdateTime = DateTimeOffset.Now;

                    newOrder.Status = status ?? Submitted;

                    if (status == Submitted)
                        newOrder.PlacedTime = DateTimeOffset.Now;
                    else if (status == Filled)
                        newOrder.FillPrice = avgFillPrice ?? lastFillPrice;

                    newOrder.History.Add(new OrderHistoryPoint() { Timestamp = DateTimeOffset.Now, Status = newOrder.Status });

                    return newOrder;
                }
                else
                    return null;
            },
            (key, oldValue) =>
            {
                logger.Info($"Received update notification for order {orderId} ({status})");

                if (permId > 0 && oldValue.PermanentID == 0)
                    oldValue.PermanentID = permId;

                if (status.HasValue)
                {
                    oldValue.Status = status.Value;

                    if (oldValue.History.LastOrDefault()?.Status != status.Value)
                        oldValue.History.Add(new OrderHistoryPoint() { Timestamp = DateTimeOffset.Now, Status = status.Value });
                }

                if (status == Filled)
                    oldValue.FillPrice = avgFillPrice ?? lastFillPrice;

                oldValue.LastUpdateTime = DateTimeOffset.Now;

                return oldValue;
            });

            brokerClient.UpdateStatus("OrdersCount", orders.Count, SystemStatusLevel.GREEN);
            OrderUpdated?.Invoke(order);
        }

        private void HandleOrderCancellation(int orderId)
        {
            // 1. Add it to orders cancelled list
            ordersCancelled.AddOrUpdate(orderId, DateTimeOffset.Now, (key, oldValue) => oldValue);

            // 2. Remove it from orders awaiting cancellation dictionary
            DateTimeOffset discarded;
            if (ordersAwaitingCancellationConfirmation.TryRemove(orderId, out discarded))
                logger.Info($"Removed order {orderId} from {nameof(ordersAwaitingCancellationConfirmation)} list");
            else
                logger.Error($"Failed to remove order {orderId} from {nameof(ordersAwaitingCancellationConfirmation)} list");
        }

        internal void StopTradingStrategyForOrder(int orderId, string message)
        {
            Order order;
            if (orders.TryGetValue(orderId, out order))
            {
                if (!string.IsNullOrEmpty(order.Strategy))
                {
                    string err = $"Requesting strategy {order.Strategy} to stop trading: {message}";

                    logger.Error(err);

                    string actionUrl = order.PermanentID > 0 && !string.IsNullOrEmpty(monitoringEndpoint) ? $"{monitoringEndpoint}/#/orders/id/{order.PermanentID}" : null;

                    SendFatal($"Stop trading for {order.Strategy}", err, actionUrl);

                    string stratName = order.Strategy.Split('-').FirstOrDefault();
                    string stratVersion = order.Strategy.Split('-').LastOrDefault();

                    tradingExecutorRunner?.StopTradingStrategy(stratName, stratVersion, message);
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
            if (isTradingConnectionLost)
                logger.Error($"Not requesting open orders: flag {nameof(isTradingConnectionLost)} is raised");
            else
                ibClient.RequestManager.OrdersRequestManager.RequestOpenOrdersFromThisClient();
        }

        private void StartOrdersPlacingQueue()
        {
            Task.Run(async () =>
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

                            if (ordersToPlaceQueue.TryDequeue(out OrderToPlace order))
                            {
                                if (order.Order.IsVirtual)
                                {
                                    long permId = -1 * long.Parse(DateTimeOffset.Now.ToString("yyyyMMddHHmmss"));

                                    if (order.Order.Type == MARKET || (order.Order.Origin != OrderOrigin.PositionClose_ContLimit && order.Order.Origin != OrderOrigin.PositionClose_ContStop))
                                    {
                                        logger.Info($"Notifying of filled virtual order {order.Order.OrderID} ({order.Order})");

                                        double? fillPrice;

                                        if (order.Order.Side == BUY)
                                            fillPrice = order.Order.LastAsk ?? (await mdConnector.GetLatest(order.Order.Cross)).RTBar?.Ask?.Close;
                                        else
                                            fillPrice = order.Order.LastBid ?? (await mdConnector.GetLatest(order.Order.Cross)).RTBar?.Bid?.Close;

                                        OnOrderStatusChangeReceived(order.Order.OrderID, Filled, order.Order.Quantity, 0, fillPrice, permId, order.Order.ParentOrderID, fillPrice);
                                    }
                                    else if (order.Order.LimitPrice.HasValue || order.Order.StopPrice.HasValue)
                                    {
                                        logger.Info($"Notifying of submitted virtual order {order.Order.OrderID} ({order.Order})");

                                        order.Order.PermanentID = permId;
                                        order.Order.Status = Submitted;

                                        openVirtualOrders.AddOrUpdate(order.Order.OrderID, order.Order, (key, oldValue) => order.Order);

                                        OnOrderStatusChangeReceived(order.Order.OrderID, Submitted, 0, order.Order.Quantity, null, permId, order.Order.ParentOrderID, null);
                                    }
                                    else
                                        logger.Error($"Unable to add virtual order {order.Order} because it has no valid limit or stop price");
                                }
                                else if (!ordersPlaced.Contains(order.Order.OrderID))
                                {
                                    logger.Debug($"OrdersPlacingQueue loop: dequeuing {order.Order.OrderID}");

                                    logger.Info($"Placing order {order.Order.OrderID}: {order.Order}");

                                    ibClient.RequestManager.OrdersRequestManager.RequestPlaceOrder(order.Order.OrderID, GetContract(order.Order.Cross), order.Order, account: order.Account, faGroup: order.FAGroup, faAllocationProfileName: order.FAAllocationProfileName);

                                    if (order.Order.Status == PreSubmitted)
                                    {
                                        logger.Debug($"Adding order {order.Order.OrderID} to the ordersAwaitingPlaceConfirmation queue");
                                        ordersAwaitingPlaceConfirmation.TryAdd(order.Order.OrderID, DateTime.Now);
                                    }
                                    else
                                        logger.Info($"Not adding order {order.Order.OrderID} to the ordersAwaitingPlaceConfirmation queue because it is not in state PreSubmitted ({order.Order.Status})"); // Sometimes when we update an existing order (ie an order that is already in state Submitted) IB doesn't send another "Submitted" status update. Therefore we need to de-activate the ordersAwaitingPlaceConfirmation check in this particular case

                                    ordersPlaced.Add(order.Order.OrderID);

                                    // Random delay in between orders
                                    Thread.Sleep((new Random()).Next(500, 1500));
                                }
                                else
                                    logger.Error($"Not trying to place order {order.Order.OrderID} again");
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
                                if (!ordersCancelled.ContainsKey(orderId))
                                {
                                    logger.Debug($"OrdersCancellingQueue loop: dequeuing {orderId}");

                                    ibClient.RequestManager.OrdersRequestManager.RequestCancelOrder(orderId);

                                    ordersAwaitingCancellationConfirmation.AddOrUpdate(orderId, DateTimeOffset.Now, (key, oldValue) => oldValue);

                                    Task.Delay(TimeSpan.FromSeconds(1)).Wait();
                                }
                                else
                                    logger.Error($"Not cancelling order {orderId} again");
                            }
                        }


                        if (loopCounter++ % 10 == 0)
                            logger.Debug($"OrdersCancellingQueue loop: {loopCounter}");

                        Task.Delay(TimeSpan.FromSeconds(0.5)).Wait();
                    }
                }
                catch (OperationCanceledException)
                {
                    logger.Warn("Stopping orders cancelling queue processing due to stop requested");
                }
            }, stopRequestedCt);
        }

        private void LoadContracts(IEnumerable<Contract> ibContracts)
        {
            if (!ibContracts.IsNullOrEmpty())
                contracts = ibContracts.Distinct(ContractCrossEqualityComparer.Instance)?.ToDictionary(c => c.Cross, c => c);
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
                var convertResult = await fxConverter.Convert(quantity, CrossUtils.GetQuotedCurrency(cross), USD, stopRequestedCt);

                if (convertResult.Success && convertResult.Converted.HasValue)
                    return (int)Math.Floor(convertResult.Converted.Value);
                else
                {
                    logger.Error($"Conversion failed: {convertResult.Message}");
                    return null;
                }
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

        private async Task<Order> CreateOrder(Cross cross, OrderSide side, int quantity, TimeInForce tif, string strategy, int? parentId, OrderOrigin origin, string groupId, string account = null)
        {
            if (cross == Cross.UNKNOWN)
            {
                logger.Error("Unable to create order: cross is unknown");
                return null;
            }

            if (side == OrderSide.UNKNOWN)
            {
                logger.Error("Unable to create order: side is unknown");
                return null;
            }

            if (quantity == 0)
            {
                logger.Error("Unable to create order: quantity is 0");
                return null;
            }

            if (tif == TimeInForce.UNKNOWN)
            {
                logger.Error("Unable to create order: time-in-force is unknown");
                return null;
            }

            var mdResult = await mdConnector.GetLatest(cross, stopRequestedCt);

            if (!mdResult.Success)
                logger.Error($"Conversion failed: {mdResult.Message}");

            Order order = new Order()
            {
                Account = account,
                Broker = Broker.IB,
                Cross = cross,
                Side = side,
                Quantity = quantity,
                TimeInForce = tif,
                OurRef = $"Strategy:{strategy}|Client:{ibClient.ClientName}",
                ParentOrderID = parentId ?? 0,
                LastAsk = mdResult.RTBar?.Ask.Close,
                LastBid = mdResult.RTBar?.Bid.Close,
                LastMid = mdResult.RTBar?.Mid.Close,
                Origin = origin,
                GroupId = groupId,
                Status = PreSubmitted,
                History = new List<OrderHistoryPoint>(),
                LastUpdateTime = DateTimeOffset.Now,
                PlacedTime = DateTimeOffset.Now,
                UsdQuantity = await GetUSDQuantity(cross, quantity),
                Strategy = strategy
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

        public async Task<Order> PlaceLimitOrder(Cross cross, OrderSide side, int quantity, double limitPrice, TimeInForce tif, string strategy, int? parentId = null, OrderOrigin origin = OrderOrigin.Unknown, string groupId = null, CancellationToken ct = default(CancellationToken))
        {
            if (!ibClient.IsConnected())
            {
                logger.Error("Cannot place LIMIT order as the IB client is not connected");
                return null;
            }

            if (isTradingConnectionLost)
            {
                logger.Error($"Cannot place LIMIT order because flag {nameof(isTradingConnectionLost)} is raised");
                return null;
            }

            // If no custom cancellation token is specified we default to the program-level stop requested token
            if (ct == null)
                ct = this.stopRequestedCt;

            if (ct.IsCancellationRequested)
            {
                logger.Error("Not placing order: operation cancelled");
                return null;
            }

            logger.Info($"Preparing LIMIT order: {side} {quantity} {cross} @ {limitPrice} (TIF: {tif})");

            // 1. Prepare the limit order
            Order order = await CreateOrder(cross, side, quantity, tif, strategy, parentId, origin, groupId);

            if (order != null)
            {
                order.Type = LIMIT;
                order.LimitPrice = limitPrice;

                // 2. Add the order to the placement queue
                logger.Debug($"Queuing LIMIT order|Side:{order.Side}|Quantity:{order.Quantity}|LimitPrice:{order.LimitPrice}|TimeInForce:{order.TimeInForce}|ParentOrderID:{order.ParentOrderID}");

                var result = await PlaceOrder(order);

                if (!result.Success)
                    logger.Error(result.Message);

                return result.Order;
            }
            else
            {
                logger.Error($"Failed to place LIMIT order (cross: {cross}, side: {side}, quantity: {quantity:N0}, limitPrice: {limitPrice}, tif: {tif})");
                return null;
            }
        }

        public async Task<Order> PlaceStopOrder(Cross cross, OrderSide side, int quantity, double stopPrice, TimeInForce tif, string strategy, int? parentId = null, OrderOrigin origin = OrderOrigin.Unknown, string groupId = null, CancellationToken ct = default(CancellationToken))
        {
            if (!ibClient.IsConnected())
            {
                logger.Error("Cannot place STOP order as the IB client is not connected");
                return null;
            }

            if (isTradingConnectionLost)
            {
                logger.Error($"Cannot place STOP order because flag {nameof(isTradingConnectionLost)} is raised");
                return null;
            }

            // If no custom cancellation token is specified we default to the program-level stop requested token
            if (ct == null)
                ct = this.stopRequestedCt;

            if (ct.IsCancellationRequested)
            {
                logger.Error("Not placing order: operation cancelled");
                return null;
            }

            logger.Info($"Preparing STOP order: {side} {quantity} {cross} @ {stopPrice} (TIF: {tif})");

            // 1. Prepare the limit order
            Order order = await CreateOrder(cross, side, quantity, tif, strategy, parentId, origin, groupId);
            if (order != null)
            {
                order.Type = STOP;
                order.StopPrice = stopPrice;

                // 2. Add the order to the placement queue
                logger.Debug($"Queuing STOP order|Cross:{order.Cross}|Side:{order.Side}|Quantity:{order.Quantity}|StopPrice:{order.StopPrice}|TimeInForce:{order.TimeInForce}|ParentOrderID:{order.ParentOrderID}");

                var result = await PlaceOrder(order);

                if (!result.Success)
                    logger.Error(result.Message);

                return result.Order;
            }
            else
            {
                logger.Error($"Failed to place LIMIT order (cross: {cross}, side: {side}, quantity: {quantity:N0}, stopPrice: {stopPrice}, tif: {tif})");
                return null;
            }
        }

        public async Task<Order> PlaceMarketOrder(Cross cross, OrderSide side, int quantity, TimeInForce tif, string strategy, int? parentId = null, OrderOrigin origin = OrderOrigin.Unknown, string groupId = null, CancellationToken ct = default(CancellationToken))
        {
            return await PlaceMarketOrder(null, cross, side, quantity, tif, strategy, parentId, origin, groupId, ct);
        }

        private async Task<Order> PlaceMarketOrder(string account, Cross cross, OrderSide side, int quantity, TimeInForce tif, string strategy, int? parentId, OrderOrigin origin, string groupId, CancellationToken ct)
        {
            if (!ibClient.IsConnected())
            {
                logger.Error("Cannot place MARKET order as the IB client is not connected");
                return null;
            }

            if (isTradingConnectionLost)
            {
                logger.Error($"Cannot place MARKET order because flag {nameof(isTradingConnectionLost)} is raised");
                return null;
            }

            // If no custom cancellation token is specified we default to the program-level stop requested token
            if (ct == null)
                ct = this.stopRequestedCt;

            if (ct.IsCancellationRequested)
            {
                logger.Error("Not placing order: operation cancelled");
                return null;
            }

            logger.Info($"Preparing MARKET order: {side} {quantity} {cross} (TIF: {tif})");

            // 1. Prepare the market order
            Order order = await CreateOrder(cross, side, quantity, tif, strategy, parentId, origin, groupId, account);

            if (order != null)
            {
                order.Type = MARKET;

                // 2. Add the order to the placement queue
                logger.Debug($"Queuing MARKET order|Cross:{order.Cross}|Side:{order.Side}|Quantity:{order.Quantity}|TimeInForce:{order.TimeInForce}|ParentOrderID:{order.ParentOrderID}");

                var result = await PlaceOrder(order);

                if (!result.Success)
                    logger.Error(result.Message);

                return result.Order;
            }
            else
            {
                logger.Error($"Failed to place MARKET order (cross: {cross}, side: {side}, quantity: {quantity:N0}, tif: {tif})");
                return null;
            }
        }

        public async Task<Order> PlaceTrailingMarketIfTouchedOrder(Cross cross, OrderSide side, int quantity, double trailingAmount, TimeInForce tif, string strategy, int? parentId = null, OrderOrigin origin = OrderOrigin.Unknown, string groupId = null, CancellationToken ct = default(CancellationToken))
        {
            if (!ibClient.IsConnected())
            {
                logger.Error("Cannot place TRAILING_MARKET_IF_TOUCHED order as the IB client is not connected");
                return null;
            }

            if (isTradingConnectionLost)
            {
                logger.Error($"Cannot place TRAILING_MARKET_IF_TOUCHED order because flag {nameof(isTradingConnectionLost)} is raised");
                return null;
            }

            // If no custom cancellation token is specified we default to the program-level stop requested token
            if (ct == null)
                ct = stopRequestedCt;

            if (ct.IsCancellationRequested)
            {
                logger.Error("Not placing order: operation cancelled");
                return null;
            }

            logger.Info($"Preparing TRAILING_MARKET_IF_TOUCHED order: {side} {quantity} {cross} @ {trailingAmount} (TIF: {tif})");

            // 1. Prepare the order
            Order order = await CreateOrder(cross, side, quantity, tif, strategy, parentId, origin, groupId);

            if (order != null)
            {
                order.Type = TRAILING_MARKET_IF_TOUCHED;
                order.TrailingAmount = trailingAmount;

                // 2. Add the order to the placement queue
                logger.Debug($"Queuing TRAILING_MARKET_IF_TOUCHED order|Cross:{order.Cross}|Side:{order.Side}|Quantity:{order.Quantity}|TrailingAmount:{order.TrailingAmount}|TimeInForce:{order.TimeInForce}|ParentOrderID:{order.ParentOrderID}");

                var result = await PlaceOrder(order);

                if (!result.Success)
                    logger.Error(result.Message);

                return result.Order;
            }
            else
            {
                logger.Error($"Failed to place TRAILING_MARKET_IF_TOUCHED order (cross: {cross}, side: {side}, quantity: {quantity:N0}, trailingAmount: {trailingAmount}, tif: {tif})");
                return null;
            }
        }

        public async Task<Order> PlaceTrailingStopOrder(Cross cross, OrderSide side, int quantity, double trailingAmount, TimeInForce tif, string strategy, int? parentId = null, OrderOrigin origin = OrderOrigin.Unknown, string groupId = null, CancellationToken ct = default(CancellationToken))
        {
            if (!ibClient.IsConnected())
            {
                logger.Error("Cannot place TRAILING_STOP order as the IB client is not connected");
                return null;
            }

            if (isTradingConnectionLost)
            {
                logger.Error($"Cannot place {TRAILING_STOP} order because flag {nameof(isTradingConnectionLost)} is raised");
                return null;
            }

            // If no custom cancellation token is specified we default to the program-level stop requested token
            if (ct == null)
                ct = this.stopRequestedCt;

            if (ct.IsCancellationRequested)
            {
                logger.Error("Not placing order: operation cancelled");
                return null;
            }

            logger.Info($"Preparing TRAILING_STOP order: {side} {quantity} {cross} @ {trailingAmount} (TIF: {tif})");

            // 1. Prepare the order
            Order order = await CreateOrder(cross, side, quantity, tif, strategy, parentId, origin, groupId);

            if (order != null)
            {
                order.Type = TRAILING_STOP;
                order.TrailingAmount = trailingAmount;

                // 2. Add the order to the placement queue
                logger.Debug($"Queuing TRAILING_STOP order|ID:{order.OrderID}|Cross:{order.Cross}|Side:{order.Side}|Quantity:{order.Quantity}|TrailingAmount:{order.TrailingAmount}|TimeInForce:{order.TimeInForce}|ParentOrderID:{order.ParentOrderID}");

                var result = await PlaceOrder(order);

                if (!result.Success)
                    logger.Error(result.Message);

                return result.Order;
            }
            else
            {
                logger.Error($"Failed to place TRAILING_STOP order (cross: {cross}, side: {side}, quantity: {quantity:N0}, trailingAmount: {trailingAmount}, tif: {tif})");
                return null;
            }
        }

        private async Task<(bool Success, string Message, Order Order)> PlaceOrder(Order order)
        {
            var faConfiguration = await RefreshFAConfiguration(); // useful to stamp up to date allocation information on the order

            lock (ordersToPlaceQueueLocker)
            {
                if (order.OrderID < 1)
                {
                    order.OrderID = GetNextValidOrderID();

                    if (order.OrderID < 0)
                    {
                        string err = "Not placing order: failed to get the next valid order ID";
                        logger.Error(err);
                        return (false, err, null);
                    }
                }
                else // This is to update an existing order
                {
                    ordersPlaced.Remove(order.OrderID);
                }

                logger.Info($"Adding order {order.OrderID} to the list");
                orders.AddOrUpdate(order.OrderID, order, (key, oldValue) => order);

                OrderToPlace orderToPlace = new OrderToPlace(order);

                if (!string.IsNullOrEmpty(order.Account))
                {
                    orderToPlace.Account = order.Account;

                    orderToPlace.Order.AllocationInfo = JsonConvert.SerializeObject(new { SingleAccount = order.Account });
                }
                else if (isInstitutionalAccount)
                {
                    string allocName = (order.Origin == OrderOrigin.PositionReverse_Open || order.Origin == OrderOrigin.PositionReverse_Close) ? $"{order.Cross}_{DoubleTicketAllocation}" : $"{order.Cross}_{SingleTicketAllocation}";

                    FAAllocationProfile allocProfile = faConfiguration?.AllocationProfiles?.FirstOrDefault(p => p.Name == allocName) ?? new FAAllocationProfile()
                    {
                        Allocations = new List<FAAllocation>(),
                        Name = allocName,
                        Type = FAAllocationProfileType.Shares
                    };

                    logger.Info($"Will use allocation {allocName} for order {order.OrderID} ({order.Origin})");
                    orderToPlace.FAAllocationProfileName = allocProfile.Name;
                    orderToPlace.Order.AllocationInfo = JsonConvert.SerializeObject(allocProfile);

                    orderToPlace.Order.Quantity = allocProfile.Allocations.Select(a => a.Amount).Sum();

                    if (orderToPlace.Order.Quantity <= 0)
                    {
                        logger.Warn($"For order {order.OrderID} the sum of allocations is 0: will place it as a virtual order");
                        order.IsVirtual = true;
                    }
                }
                else
                {
                    logger.Info($"Not using allocation for order {order.OrderID}: flag brokerClient.IsInstitutionalAccount isn't raised (value={isInstitutionalAccount}) and no specific account name was specified");
                }

                logger.Info($"Adding order {order.OrderID} to the ordersToPlace queue");
                ordersToPlaceQueue.Enqueue(orderToPlace);

                return (true, null, order);
            }
        }

        public async Task<GenericActionResult> CancelOrder(int orderId, CancellationToken ct = default(CancellationToken))
        {
            var confirmedCancelled = IsConfirmedCancelled(orderId);
            if (confirmedCancelled.Item1)
            {
                string msg = $"Order {orderId} was already confirmed cancelled at {confirmedCancelled.Item2}";
                logger.Info(msg);
                return new GenericActionResult(true, msg);
            }

            if (!ibClient.IsConnected())
            {
                string err = "Cannot cancel order as the IB client is not connected";
                logger.Error(err);
                return new GenericActionResult(false, err);
            }

            if (isTradingConnectionLost)
            {
                string err = $"Cannot cancel order because flag {nameof(isTradingConnectionLost)} is raised";
                logger.Error(err);
                return new GenericActionResult(false, err);
            }

            // If no custom cancellation token is specified we default to the program-level stop requested token
            if (ct == null)
                ct = stopRequestedCt;

            if (ct.IsCancellationRequested)
            {
                string err = "Not cancelling order: operation cancelled";
                logger.Error(err);
                return new GenericActionResult(false, err);
            }

            logger.Warn($"Cancelling order {orderId}");

            ordersToCancelQueue.Enqueue(orderId);

            return await Task.Run(() =>
            {
                try
                {
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();

                        confirmedCancelled = IsConfirmedCancelled(orderId);
                        if (confirmedCancelled.Item1)
                            return new GenericActionResult(true, $"Order {orderId} was confirmed cancelled at {confirmedCancelled.Item2}");

                        string cancelFailedError;

                        if (failedOrderCancellationRequests.TryRemove(orderId, out cancelFailedError))
                            return new GenericActionResult(false, cancelFailedError);

                        Task.Delay(TimeSpan.FromSeconds(1)).Wait();
                    }
                }
                catch (OperationCanceledException)
                {
                    string err = $"Failed to cancel order {orderId}: operation cancelled";
                    logger.Error(err);
                    return new GenericActionResult(false, err);
                }
                catch (Exception ex)
                {
                    string err = $"Failed to cancel order {orderId}";
                    logger.Error(err, ex);
                    return new GenericActionResult(false, $"{err}: {ex.Message}");
                }
            });
        }

        private (bool, DateTimeOffset) IsConfirmedCancelled(int orderId, CancellationToken ct = default(CancellationToken))
        {
            DateTimeOffset cancellationTimestamp;

            // 1. Check if that order is already in the cancelled list
            if (ordersCancelled.TryGetValue(orderId, out cancellationTimestamp))
                return (true, cancellationTimestamp);

            // 2. Try to force a refresh
            RequestOpenOrders();

            Task.Delay(TimeSpan.FromSeconds(1)).Wait();

            // 3. And check again if that order is already in the cancelled list
            if (ordersCancelled.TryGetValue(orderId, out cancellationTimestamp))
                return (true, cancellationTimestamp);
            else
                return (false, DateTimeOffset.MinValue);
        }

        public async Task<GenericActionResult> CancelAllOrders(IEnumerable<Cross> crosses, CancellationToken ct = default(CancellationToken))
        {
            if (!ibClient.IsConnected())
            {
                string err = "Cannot cancel all orders as the IB client is not connected";
                logger.Error(err);
                return new GenericActionResult(false, err);
            }

            if (isTradingConnectionLost)
            {
                string err = $"Cannot cancel all orders because flag {nameof(isTradingConnectionLost)} is raised";
                logger.Error(err);
                return new GenericActionResult(false, err);
            }

            // If no custom cancellation token is specified we default to the program-level stop requested token
            if (ct == null)
                ct = this.stopRequestedCt;

            if (ct.IsCancellationRequested)
            {
                string err = "Not requesting cancel of active orders: operation cancelled";
                logger.Error(err);
                return new GenericActionResult(false, err);
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

                Dictionary<int, string> failedCancels = new Dictionary<int, string>();

                foreach (int orderId in ordersToCancel)
                {
                    var result = await CancelOrder(orderId);

                    if (!result.Success)
                    {
                        logger.Error($"Failed to cancel order {orderId}: {result.Message}");
                        failedCancels.Add(orderId, result.Message);
                    }

                    await Task.Delay(TimeSpan.FromSeconds(2));
                }

                if (failedCancels.IsNullOrEmpty())
                    return new GenericActionResult(true, $"Successfully cancelled orders {string.Join(", ", ordersToCancel)}");
                else
                    return new GenericActionResult(false, $"Failed to cancel some orders: {string.Join(", ", failedCancels.Select(kvp => $"{kvp.Key} ({kvp.Value})"))}");
            }

            return new GenericActionResult(true, "Found no active order to cancel");
        }

        public async Task<(bool Success, string Message, Dictionary<Tuple<string, Cross>, double?> ClosedPositions)> CloseAllPositions(IEnumerable<Cross> crosses, OrderOrigin origin = OrderOrigin.PositionClose_TE, CancellationToken ct = default(CancellationToken))
        {
            if (!ibClient.IsConnected())
            {
                string err = "Cannot close any position as the IB client is not connected";
                logger.Error(err);
                return (false, err, null);
            }

            if (isTradingConnectionLost)
            {
                string err = $"Cannot close any position because flag {nameof(isTradingConnectionLost)} is raised";
                logger.Error(err);
                return (false, err, null);
            }

            // If no custom cancellation token is specified we default to the program-level stop requested token
            if (ct == null)
                ct = stopRequestedCt;

            try
            {
                ct.ThrowIfCancellationRequested();
                // 1. Get all open postions
                var positionResult = await brokerClient.TwsServiceConnector.PositionsConnector.RequestPositions(ct);

                if (positionResult.Success)
                {
                    if (!positionResult.Positions.IsNullOrEmpty())
                    {
                        Dictionary<Tuple<string, Cross>, double> openPositions = positionResult.Positions.Where(p => crosses.Contains(p.Cross) && p.PositionQuantity != 0).ToDictionary(p => new Tuple<string, Cross>(p.Account, p.Cross), p => p.PositionQuantity);

                        if (openPositions.IsNullOrEmpty())
                        {
                            string msg = "No open position: nothing to close";
                            logger.Info(msg);
                            return (true, msg, new Dictionary<Tuple<string, Cross>, double?>());
                        }
                        else
                        {
                            // 2. Place market orders to close all open positions
                            Dictionary<Tuple<string, Cross>, double?> retVal = new Dictionary<Tuple<string, Cross>, double?>();
                            List<int> closingOrders = new List<int>();

                            foreach (var pos in openPositions)
                            {
                                logger.Info($"Closing position: {pos.Value} {pos.Key}");

                                OrderSide side = pos.Value > 0 ? SELL : BUY;

                                Order order = await PlaceMarketOrder(pos.Key.Item1, pos.Key.Item2, side, Math.Abs((int)Math.Floor(pos.Value)), TimeInForce.DAY, "CloseAllPositions", null, origin, Guid.NewGuid().ToString(), ct);

                                if (order != null)
                                {
                                    logger.Debug($"Successfully closed position: {pos.Value} {pos.Key} (order {order})");
                                    closingOrders.Add(order.OrderID);
                                }
                                else
                                    logger.Error($"Failed to close position: {pos.Value} {pos.Key}");

                                retVal.Add(pos.Key, null);
                            }

                            CancellationTokenSource cts = new CancellationTokenSource();
                            cts.CancelAfter(TimeSpan.FromMinutes(1));

                            await Task.Run(() =>
                            {
                                while (closingOrders.Count > 0)
                                {
                                    List<int> filledOrders = new List<int>();

                                    foreach (var orderId in closingOrders)
                                    {
                                        Order order;
                                        if (orders.TryGetValue(orderId, out order) && order.Status == Filled)
                                        {
                                            retVal[new Tuple<string, Cross>(order.Account, order.Cross)] = order.FillPrice;
                                            filledOrders.Add(orderId);
                                        }
                                    }

                                    if (filledOrders.Count > 0)
                                    {
                                        foreach (var orderId in filledOrders)
                                            closingOrders.Remove(orderId);
                                    }

                                    if (filledOrders.Count > 0)
                                        Task.Delay(TimeSpan.FromSeconds(5)).Wait();
                                }
                            }, cts.Token);

                            return (true, $"Successfully closed positions for {string.Join(", ", crosses)}", retVal);
                        }
                    }
                    else
                    {
                        string msg = "No open position received from TWS service: nothing to close";
                        logger.Info(msg);
                        return (true, msg, new Dictionary<Tuple<string, Cross>, double?>());
                    }
                }
                else
                {
                    string err = $"Failed to request positions from TWS service: {positionResult.Message}";
                    logger.Error(err);
                    return (false, err, null);
                }
            }
            catch (OperationCanceledException)
            {
                string err = "Not closing positions: operation cancelled";
                logger.Error(err);
                return (false, err, null);
            }
            catch (Exception ex)
            {
                string err = "Failed to close positions";
                logger.Error(err, ex);
                return (false, $"{err}: {ex.Message}", null);
            }
        }

        public async Task<GenericActionResult<Order>> UpdateOrderLevel(int orderId, double newLevel, int? newQuantity = null, CancellationToken ct = default(CancellationToken))
        {
            await Task.CompletedTask;

            if (!ibClient.IsConnected())
            {
                string err = "Cannot update order level as the IB client is not connected";
                logger.Error(err);
                return new GenericActionResult<Order>(false, err, null);
            }

            if (isTradingConnectionLost)
            {
                string err = $"Cannot update order level because flag {nameof(isTradingConnectionLost)} is raised";
                logger.Error(err);
                return new GenericActionResult<Order>(false, err, null);
            }

            Order currentOrder;

            if (orders.TryGetValue(orderId, out currentOrder))
            {
                if (currentOrder.Status != Submitted && currentOrder.Status != PreSubmitted)
                {
                    string err = $"Order {orderId} cannot be updated as it is in state {currentOrder.Status}";
                    logger.Error(err);
                    return new GenericActionResult<Order>(false, err);
                }

                switch (currentOrder.Type)
                {
                    case LIMIT:
                        if (currentOrder.LimitPrice != newLevel || (newQuantity.HasValue && newQuantity.Value != currentOrder.Quantity))
                        {
                            logger.Info($"Updating level of LIMIT order {orderId} from {currentOrder.LimitPrice} to {newLevel}");

                            currentOrder.LimitPrice = newLevel;

                            if (newQuantity.HasValue && newQuantity.Value > 0)
                                currentOrder.Quantity = newQuantity.Value;

                            var limitUpdateResult = await PlaceOrder(currentOrder);

                            if (limitUpdateResult.Success)
                            {
                                AddOrderHistoryPoint(orderId, new OrderHistoryPoint()
                                {
                                    Message = $"Updated limit price to {newLevel}",
                                    Status = limitUpdateResult.Order.Status,
                                    Timestamp = DateTimeOffset.Now
                                });

                                return new GenericActionResult<Order>(true, $"Updated limit price of order {orderId} to {newLevel}", limitUpdateResult.Order);
                            }
                            else
                                return new GenericActionResult<Order>(false, $"Failed to update limit price of order {orderId} to {newLevel}: {limitUpdateResult.Message}");
                        }
                        else
                        {
                            string msg = $"No need to update order {orderId}: price and quantity are unchanged";
                            logger.Info(msg);
                            return new GenericActionResult<Order>(true, msg, currentOrder);
                        }
                    case STOP:
                        if (currentOrder.StopPrice != newLevel || (newQuantity.HasValue && newQuantity.Value != currentOrder.Quantity))
                        {
                            logger.Info($"Updating level of STOP order {orderId} from {currentOrder.StopPrice} to {newLevel}");

                            currentOrder.StopPrice = newLevel;

                            if (newQuantity.HasValue && newQuantity.Value > 0)
                                currentOrder.Quantity = newQuantity.Value;

                            var stopUpdateResult = await PlaceOrder(currentOrder);

                            if (stopUpdateResult.Success)
                            {
                                AddOrderHistoryPoint(orderId, new OrderHistoryPoint()
                                {
                                    Message = $"Updated stop price to {newLevel}",
                                    Status = stopUpdateResult.Order.Status,
                                    Timestamp = DateTimeOffset.Now
                                });

                                return new GenericActionResult<Order>(true, $"Updated stop price of order {orderId} to {newLevel}", stopUpdateResult.Order);
                            }
                            else
                                return new GenericActionResult<Order>(false, $"Failed to stop price of order {orderId} to {newLevel}: {stopUpdateResult.Message}");
                        }
                        else
                        {
                            string msg = $"No need to update order {orderId}: price and quantity are unchanged";
                            logger.Info(msg);
                            return new GenericActionResult<Order>(true, msg, currentOrder);
                        }
                    default:
                        string err = $"Updating the level of orders of type {currentOrder.Type} is not supported";
                        logger.Error(err);
                        return new GenericActionResult<Order>(false, err);
                }
            }
            else
            {
                string err = $"Order {orderId} is unknown. Unable to update its level";
                logger.Error(err);
                return new GenericActionResult<Order>(false, err, null);
            }
        }

        private void AddOrderHistoryPoint(int orderId, OrderHistoryPoint point)
        {
            if (orders.ContainsKey(orderId))
            {
                orders.AddOrUpdate(orderId, (key) =>
                {
                    logger.Error($"Unable to add history point to unknown order {orderId}");
                    return null;
                }, (key, oldValue) =>
                {
                    oldValue.History.Add(point);
                    return oldValue;
                });
            }
            else
                logger.Error($"Unable to add history point to unknown order {orderId}");
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

        internal bool HandleTradingDisconnection()
        {
            // Throttle notifications
            if (DateTimeOffset.Now.Subtract(lastTradingConnectionLostCheck) > TimeSpan.FromSeconds(5))
            {
                lock (lastTradingConnectionLostCheckLocker)
                {
                    lastTradingConnectionLostCheck = DateTimeOffset.Now;
                }

                SetIsTradingConnectionLostFlag(true);

                logger.Warn("Trading connection is lost: notifying interested parties");

                TradingConnectionLost?.Invoke();

                return true;
            }
            else
                return false; // No need to relay the error again
        }

        internal bool HandleTradingReconnection()
        {
            // Throttle notifications
            if (DateTimeOffset.Now.Subtract(lastTradingConnectionResumedCheck) > TimeSpan.FromSeconds(5))
            {
                lock (lastTradingConnectionResumedCheckLocker)
                {
                    lastTradingConnectionResumedCheck = DateTimeOffset.Now;
                }

                SetIsTradingConnectionLostFlag(false);

                logger.Warn("Trading connection is resumed: notifying interested parties");

                TradingConnectionResumed?.Invoke();

                return true;
            }
            else
                return false; // No need to relay the error again
        }

        private void SetIsTradingConnectionLostFlag(bool value)
        {
            logger.Info($"Setting {nameof(isTradingConnectionLost)} flag to {value}");

            lock (isTradingConnectionLostLocker)
            {
                isTradingConnectionLost = value;
            }
        }

        public void ResetTradingConnectionStatus(bool isConnected)
        {
            SetIsTradingConnectionLostFlag(!isConnected);
        }

        public bool RequestTradingConnectionStatus()
        {
            return !isTradingConnectionLost;
        }

        private async Task<FAConfiguration> RefreshFAConfiguration()
        {
            try
            {
                CancellationTokenSource cts = new CancellationTokenSource();
                cts.CancelAfter(TimeSpan.FromSeconds(10));

                var result = await brokerClient.TwsServiceConnector.FAConfigurationsConnector.RequestFAConfiguration(cts.Token);

                if (!result.Success && result.Message.Contains("another request is already in progress"))
                {
                    Task.Delay(TimeSpan.FromSeconds(1.5)).Wait();
                    result = await brokerClient.TwsServiceConnector.FAConfigurationsConnector.RequestFAConfiguration(cts.Token);
                }

                if (result.Success && result.FAConfiguration != null)
                {
                    var accounts = result.FAConfiguration?.AccountAliases.Select(a => a.Account)?.ToArray();
                    int accountsCount = accounts?.Length ?? 0;

                    if (accountsCount > 1)
                        logger.Info($"The user connected to this TWS manages {accountsCount} accounts ({string.Join(", ", accounts)}): this is an institutional account");
                    else
                        logger.Info($"The user connected to this TWS only manages one account ({accounts?.FirstOrDefault()}): this is an individual account");

                    SetIsInstitutionalAccountFlag(accountsCount > 1);

                    lastKnownFAConfiguration = result.FAConfiguration;
                }
                else
                {
                    logger.Error($"Failed to refresh FA configuration: {result.Message}. Will reuse last known FA config, if any");
                }
            }
            catch (Exception ex)
            {
                logger.Error("Failed to refresh FA configuration. Will reuse last known FA config, if any", ex);
            }

            return lastKnownFAConfiguration;
        }

        private void SetIsInstitutionalAccountFlag(bool value)
        {
            if (isInstitutionalAccount == value)
                return;

            logger.Info($"Setting flag {nameof(isInstitutionalAccount)} to {value}");

            lock (isInstitutionalAccountLocker)
            {
                isInstitutionalAccount = value;
            }
        }

        public async Task<(bool Success, string Message)> FillAnyVirtualOrder(PriceTick priceTick = null, RTBar rtBar = null, CancellationToken ct = default(CancellationToken))
        {
            if (priceTick == null && rtBar == null)
            {
                string err = "Unable to check open orders to fill. At least one of the priceTick or rtBar must be not null";
                logger.Error(err);
                return (false, err);
            }

            Cross cross = priceTick?.Cross ?? rtBar.Cross;
            double? askPrice = (priceTick?.TickType == MarketDataTickType.ASK) ? priceTick.Price : rtBar?.Ask?.Close;
            double? bidPrice = (priceTick?.TickType == MarketDataTickType.BID) ? priceTick.Price : rtBar?.Bid?.Close;

            if (!askPrice.HasValue && !bidPrice.HasValue)
            {
                string err = "Unable to check fillable orders: askPrice and bidPrice are both undefined";
                logger.Error(err);
                return (false, err);
            }

            // 1. Make a copy of the current open orders
            var virtualOrders = openVirtualOrders.ToArray();

            // 2. Identify any valid order
            IEnumerable<int> fillableOrderIds = virtualOrders.Select(kvp => kvp.Value).Where(o =>
            {
                if (o.Cross != cross)
                    return false;

                switch (o.Side)
                {
                    case BUY:
                        if (askPrice.HasValue && (o.LimitPrice >= askPrice.Value || o.StopPrice <= askPrice.Value))
                        {
                            logger.Info($"Order {o.OrderID} is fillable: Side=BUY|LimitPrice={o.LimitPrice}|StopPrice={o.StopPrice}|LastAsk={askPrice}");
                            return true;
                        }
                        else
                            return false;
                    case SELL:
                        if (bidPrice.HasValue && (o.LimitPrice <= bidPrice.Value || o.StopPrice >= bidPrice.Value))
                        {
                            logger.Info($"Order {o.OrderID} is fillable: Side=SELL|LimitPrice={o.LimitPrice}|StopPrice={o.StopPrice}|LastBid={bidPrice}");
                            return true;
                        }
                        else
                            return false;
                    default:
                        return false;
                }
            }).Select(o => o.OrderID);

            // 3. Fill orders
            foreach (int orderId in fillableOrderIds)
            {
                if (openVirtualOrders.TryRemove(orderId, out Order orderToFill))
                {
                    double? fillPrice = (orderToFill.Side == BUY) ? askPrice : bidPrice;
                    OnOrderStatusChangeReceived(orderId, Filled, orderToFill.Quantity, 0, fillPrice, orderToFill.PermanentID, orderToFill.ParentOrderID, fillPrice);
                }
            }

            string msg = !fillableOrderIds.IsNullOrEmpty() ? $"Filled {fillableOrderIds.Count()} virtual orders: {string.Join(", ", fillableOrderIds)}" : "No fillable virtual order";

            logger.Info(msg);
            return (true, msg);
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

        private class OrderToPlace
        {
            public Order Order { get; private set; }

            public string Account { get; set; }
            public string FAAllocationProfileName { get; set; }
            public FAGroup FAGroup { get; set; }

            public OrderToPlace(Order order)
            {
                Order = order;
            }
        }
    }
}
