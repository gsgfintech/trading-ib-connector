using log4net;
using log4net.Config;
using static Net.Teirlinck.FX.Data.OrderData.OrderSide;
using static Net.Teirlinck.FX.Data.OrderData.TimeInForce;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Net.Teirlinck.FX.Data.ExecutionData;
using Net.Teirlinck.FX.Data.ContractData;
using static Net.Teirlinck.FX.Data.ContractData.Cross;
using Net.Teirlinck.FX.Data.AccountPortfolioData;
using Net.Teirlinck.FX.Data.OrderData;
using System.Linq;
using Capital.GSG.FX.Trading.Executor;
using Capital.GSG.FX.MonitoringAppConnector;
using Net.Teirlinck.FX.FXTradingMongoConnector;
using System.Collections.Concurrent;
using Capital.GSG.FX.MarketDataService.Connector;
using Capital.GSG.FX.FXConverter;
using Capital.GSG.FX.FXConverterServiceConnector;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI.Executor
{
    class Program
    {
        private static ILog logger = LogManager.GetLogger(nameof(Program));

        private static MongoDBServer mongoDBServer = null;

        private static IFxConverter fxConverter;
        private static PositionsConnector positionsConnector;
        private static ExecutionsConnector executionsConnector;
        private static OrdersConnector ordersConnector = null;
        private static MDConnector mdConnector = null;

        private static CancellationTokenSource stopRequestedCts = new CancellationTokenSource();

        private static object locker = new object();

        private static List<Position> latestPositionsUpdate = new List<Position>();
        private static bool positionUpdatePosted = false;
        private static Timer positionUpdatePosterTimer = null;

        internal static int RtBarsCounter = 0;

        private static ConcurrentDictionary<int, int> orderRequests = new ConcurrentDictionary<int, int>();

        static void Main(string[] args)
        {
            XmlConfigurator.Configure();

            Dictionary<string, object> brokerClientConfig = new Dictionary<string, object>() {
                { "Name", "IB_MDClient_Test" },
                { "ClientNumber", 7 },
                { "Host", "gsg-dev-1.gsg.capital" },
                { "Port", 7497 },
                { "IBControllerPort", 7463 },
                { "IBControllerServiceEndpoint", "https://gsg-dev-1.gsg.capital:6583" },
                { "IBControllerServiceAppName", "TwsPaper" },
                { "TradingAccount", "DU215795" }
            };

            string mongoDBHost = "tryphon.gsg.capital";
            int mongoDBPort = 27020;
            string mongoDBName = "fxtrading_dev";
            string monitoringEndpoint = "http://localhost:51468/";
            string convertServiceEndpoint = "https://gsg-dev-1.gsg.capital:6580";
            string marketDataServiceEndpoint = "https://tryphon.gsg.capital:6581";

            logger.Info("Starting service IBExecutorTester");

            logger.Debug($"BrokerClientConfigId: {brokerClientConfig}");
            logger.Debug($"MongoDBHost: {mongoDBHost}");
            logger.Debug($"MongoDBPort: {mongoDBPort}");
            logger.Debug($"MongoDBName: {mongoDBName}");
            logger.Debug($"MonitoringEndpoint: {monitoringEndpoint}");
            logger.Debug($"ConvertServiceEndpoint: {convertServiceEndpoint}");
            logger.Debug($"MDConnector: {marketDataServiceEndpoint}");

            fxConverter = ConvertConnector.GetConnector(convertServiceEndpoint);
            positionsConnector = PositionsConnector.GetConnector(monitoringEndpoint);
            executionsConnector = ExecutionsConnector.GetConnector(monitoringEndpoint);
            ordersConnector = OrdersConnector.GetConnector(monitoringEndpoint);
            mdConnector = MDConnector.GetConnector(marketDataServiceEndpoint);

            mongoDBServer = MongoDBServer.CreateServer(mongoDBName, mongoDBHost, mongoDBPort);

            Do(brokerClientConfig, mongoDBHost, mongoDBPort, mongoDBName).Wait();
        }

        private static async Task Do(Dictionary<string, object> brokerClientConfig, string mongoDBHost, int mongoDBPort, string mongoDBName)
        {
            try
            {
                IBTestTradingExecutorRunner tradingExecutorRunner = new IBTestTradingExecutorRunner();

                AutoResetEvent stopCompleteEvent = new AutoResetEvent(false);

                IBrokerClient brokerClient = await BrokerClient.SetupBrokerClient(IBrokerClientType.Trading, tradingExecutorRunner, brokerClientConfig, mongoDBServer, fxConverter, mdConnector, null, stopRequestedCts.Token, false);
                ((BrokerClient)brokerClient).StopComplete += (() => stopCompleteEvent.Set());
                ((BrokerClient)brokerClient).AlertReceived += (alert) =>
                {
                    logger.Error(alert);
                };

                //brokerClient.OrderExecutor.OrderUpdated += Program_OrderUpdated;
                //brokerClient.OrderExecutor.RequestCompleted += async (requestId, orderId, status, lastFillPrice) =>
                //{
                //    if (orderRequests.TryUpdate(requestId, orderId, -1))
                //    {
                //        logger.Info($"Updated request {requestId} with orderID {orderId}");

                //        if (status == OrderRequestStatus.PendingFill)
                //        {
                //            await brokerClient.OrderExecutor.UpdateOrderLevel(orderId, 1.116);
                //        }
                //    }
                //};

                //brokerClient.TradesExecutor.TradeReceived += TradesExecutor_TradeReceived;
                //brokerClient.PositionExecutor.PositionUpdated += PositionExecutor_PositionsUpdateReceived;
                //brokerClient.PositionExecutor.AccountUpdated += PositionExecutor_AccountUpdated;

                // Need to throttle position updates, as there are lots of them
                positionUpdatePosterTimer = new Timer((state) =>
                {
                    if (latestPositionsUpdate?.Count() > 0 && !positionUpdatePosted)
                    {
                        foreach (Position position in latestPositionsUpdate)
                            positionsConnector?.PostPosition(position, ct: stopRequestedCts.Token);

                        latestPositionsUpdate = new List<Position>();

                        lock (locker)
                        {
                            positionUpdatePosted = true;
                        }
                    }
                }, null, 1000, 1000);

                //await SubscribeAndListenRTBars(brokerClient);

                //await PlaceLimitOrders(brokerClient.OrderExecutor);
                //await PlaceAndUpdateLimitOrders(brokerClient.OrderExecutor);

                //await PlaceMarketOrders(brokerClient.OrderExecutor);

                //await PlaceStopOrders(brokerClient.OrderExecutor);
                //await PlaceAndUpdateStopOrders(brokerClient.OrderExecutor);

                //await PlaceTrailingMarketIfTouchedOrders(brokerClient.OrderExecutor);

                //await PlaceTrailingStopOrders(brokerClient.OrderExecutor);

                logger.Debug("Sleeping for 10 seconds");
                Task.Delay(TimeSpan.FromSeconds(10)).Wait();

                //await CancelAllOrdersAndClosePositions(brokerClient.OrderExecutor);

                //await RestartTws((BrokerClient)brokerClient);

                Console.WriteLine("Press a key to exit");
                Console.ReadLine();

                stopRequestedCts.Cancel();

                brokerClient.Dispose();

                stopCompleteEvent?.WaitOne();
            }
            catch (Exception ex)
            {
                logger.Fatal("Caught fatal unhandled exception. The process will now exit", ex);
            }
        }

        private static async Task RestartTws(BrokerClient client)
        {
            if (await client.Restart())
                logger.Info("Successfully shutdown TWS");
            else
                logger.Error("Failed to shutdown TWS");
        }

        private static async Task CancelAllOrdersAndClosePositions(IOrderExecutor executor)
        {
            if (await executor.CancelAllOrders(CrossUtils.AllCrosses))
            {
                logger.Info("Cancelled all orders");

                if (await executor.CloseAllPositions(CrossUtils.AllCrosses))
                    logger.Info("Closed all positions");
                else
                    logger.Error("Failed to close all positions");
            }
            else
                logger.Error("Failed to cancel all orders");
        }

        private static async void Program_OrderUpdated(Order order)
        {
            logger.Info($"Received order update: ID {order.OrderID}");

            CancellationTokenSource cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            await mongoDBServer?.OrderActioner.AddOrUpdate(order, ct: cts.Token);

            await ordersConnector?.PostOrder(order, ct: cts.Token);
        }

        private static async void PositionExecutor_AccountUpdated(Account account)
        {
            Console.WriteLine("Received account update:");
            //Console.WriteLine(account.ToString());

            CancellationTokenSource cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            await mongoDBServer?.AccountActioner.InsertOrUpdate(account, ct: cts.Token);
        }

        private static async Task<Dictionary<Cross, Contract>> GetContracts(string mongoDBHost, int mongoDBPort, string mongoDBName)
        {
            Contract eurContract = await mongoDBServer?.ContractActioner.GetByCross(EURUSD);
            Contract chfContract = await mongoDBServer?.ContractActioner.GetByCross(USDHKD);

            Dictionary<Cross, Contract> contracts = new Dictionary<Cross, Contract>();
            contracts.Add(EURUSD, eurContract);
            contracts.Add(EURCHF, chfContract);

            return contracts;
        }

        private static async void TradesExecutor_TradeReceived(Execution execution)
        {
            logger.Info($"Received execution of {CrossUtils.ToSring(execution.Cross)}: {execution}");

            CancellationTokenSource cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            await mongoDBServer?.ExecutionActioner.Insert(execution, cts.Token);

            await executionsConnector?.PostNewExecution(execution, ct: cts.Token);
        }

        private static async Task PlaceTrailingStopOrders(IOrderExecutor orderExecutor)
        {
            int order1Id = await orderExecutor.PlaceTrailingStopOrder(EURUSD, BUY, 20000, 0.001, DAY, "IBExecutorTester", "1.0", ct: stopRequestedCts.Token);
            Console.WriteLine("TrailingStopOrder1: {0} ({1})", order1Id > 0 ? "SUCCESS" : "FAILED", order1Id);
            Thread.Sleep(1000);

            int order2Id = await orderExecutor.PlaceTrailingStopOrder(EURUSD, SELL, 20000, 0.001, DAY, "IBExecutorTester", "1.0", ct: stopRequestedCts.Token);
            Console.WriteLine("TrailingStopOrder2: {0} ({1})", order2Id > 0 ? "SUCCESS" : "FAILED", order2Id);
            Thread.Sleep(1000);
        }

        private static async Task PlaceTrailingMarketIfTouchedOrders(IOrderExecutor orderExecutor)
        {
            int order1Id = await orderExecutor.PlaceTrailingMarketIfTouchedOrder(EURUSD, BUY, 20000, 0.001, DAY, "IBExecutorTester", "1.0", ct: stopRequestedCts.Token);
            Console.WriteLine("TrailingMarketIfTouchedOrder1: {0} ({1})", order1Id > 0 ? "SUCCESS" : "FAILED", order1Id);
            Thread.Sleep(1000);

            int order2Id = await orderExecutor.PlaceTrailingMarketIfTouchedOrder(EURUSD, SELL, 20000, 0.001, DAY, "IBExecutorTester", "1.0", ct: stopRequestedCts.Token);
            Console.WriteLine("TrailingMarketIfTouchedOrder2: {0} ({1})", order2Id > 0 ? "SUCCESS" : "FAILED", order2Id);
            Thread.Sleep(1000);
        }

        private static async Task PlaceMarketOrders(IOrderExecutor orderExecutor)
        {
            int order1Id = await orderExecutor.PlaceMarketOrder(EURUSD, SELL, 20000, DAY, "IBExecutorTester", "1.0", ct: stopRequestedCts.Token);
            Console.WriteLine("MarketOrder1: {0} ({1})", order1Id > 0 ? "SUCCESS" : "FAILED", order1Id);
            Thread.Sleep(1000);

            int order2Id = await orderExecutor.PlaceMarketOrder(EURCHF, SELL, 20000, DAY, "IBExecutorTester", "1.0", ct: stopRequestedCts.Token);
            Console.WriteLine("MarketOrder2: {0} ({1})", order2Id > 0 ? "SUCCESS" : "FAILED", order2Id);
            Thread.Sleep(1000);
        }

        private static async Task PlaceLimitOrders(IOrderExecutor orderExecutor)
        {
            int order1Id = await orderExecutor.PlaceLimitOrder(EURUSD, SELL, 20000, 1.125, DAY, "IBExecutorTester", "1.0", ct: stopRequestedCts.Token);
            Console.WriteLine("LimitOrder1: {0} ({1})", order1Id > 0 ? "SUCCESS" : "FAILED", order1Id);
            Thread.Sleep(1000);

            int order2Id = await orderExecutor.PlaceLimitOrder(EURUSD, BUY, 20000, 1.115, DAY, "IBExecutorTester", "1.0", ct: stopRequestedCts.Token);
            Console.WriteLine("LimitOrder2: {0} ({1})", order2Id > 0 ? "SUCCESS" : "FAILED", order2Id);
            Thread.Sleep(1000);
        }

        private static async Task PlaceAndUpdateLimitOrders(IOrderExecutor orderExecutor)
        {
            int orderId = await orderExecutor.PlaceLimitOrder(EURUSD, BUY, 20000, 1.115, DAY, "IBExecutorTester", "1.0", ct: stopRequestedCts.Token);

            if (orderId > -1)
            {
                Task.Delay(TimeSpan.FromSeconds(5)).Wait();
                await orderExecutor.UpdateOrderLevel(orderId, 1.116);
            }

            Console.WriteLine("LimitOrder2: {0} ({1})", orderId > 0 ? "SUCCESS" : "FAILED", orderId);
            Task.Delay(TimeSpan.FromSeconds(1)).Wait();
        }

        private static async Task PlaceStopOrders(IOrderExecutor orderExecutor)
        {
            int order1Id = await orderExecutor.PlaceStopOrder(EURUSD, SELL, 20000, 1.115, DAY, "IBExecutorTester", "1.0", ct: stopRequestedCts.Token);
            Console.WriteLine("StopOrder1: {0} ({1})", order1Id > 0 ? "SUCCESS" : "FAILED", order1Id);
            Thread.Sleep(1000);

            int order2Id = await orderExecutor.PlaceStopOrder(EURUSD, BUY, 20000, 1.120, DAY, "IBExecutorTester", "1.0", ct: stopRequestedCts.Token);
            Console.WriteLine("StopOrder2: {0} ({1})", order2Id > 0 ? "SUCCESS" : "FAILED", order2Id);
            Thread.Sleep(1000);
        }

        private static async Task PlaceAndUpdateStopOrders(IOrderExecutor orderExecutor)
        {
            int orderId = await orderExecutor.PlaceStopOrder(EURUSD, SELL, 20000, 1.115, DAY, "IBExecutorTester", "1.0", ct: stopRequestedCts.Token);

            if (orderId > -1)
            {
                Task.Delay(TimeSpan.FromSeconds(5)).Wait();
                await orderExecutor.UpdateOrderLevel(orderId, 1.116);
            }

            Console.WriteLine("StopOrder1: {0} ({1})", orderId > 0 ? "SUCCESS" : "FAILED", orderId);
            Task.Delay(TimeSpan.FromSeconds(1)).Wait();
        }

        private static async void PositionExecutor_PositionsUpdateReceived(Position position)
        {
            logger.Info($"Received positions details:");

            try
            {
                CancellationTokenSource cts = new CancellationTokenSource();
                cts.CancelAfter(TimeSpan.FromSeconds(5));

                Console.WriteLine(position);

                await mongoDBServer?.PositionActioner.Insert(position, cts.Token);

                lock (locker)
                {
                    latestPositionsUpdate.Add(position);
                    positionUpdatePosted = false;
                }
            }
            catch (OperationCanceledException)
            {
                logger.Error("Failed to handle position update: operation cancelled");
            }
            catch (Exception ex)
            {
                logger.Error("Failed to handle position update", ex);
            }
        }

        private static async Task SubscribeAndListenRTBars(IBrokerClient brokerClient)
        {
            await Task.Run(() =>
            {
                brokerClient.MarketDataProvider.SubscribeRTBars(new Cross[7] {
                     EURUSD, NZDUSD, USDJPY, USDCHF, GBPUSD, USDCAD, AUDUSD
                 });

                while (RtBarsCounter < 10 * 7)
                {
                    Task.Delay(500).Wait();
                }

                brokerClient.MarketDataProvider.UnsubscribeRTBars();
            });
        }
    }
}
