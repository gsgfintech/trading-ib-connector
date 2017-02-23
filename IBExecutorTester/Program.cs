using log4net;
using log4net.Config;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Capital.GSG.FX.MonitoringAppConnector;
using System.Collections.Concurrent;
using Capital.GSG.FX.MarketDataService.Connector;
using Capital.GSG.FX.FXConverter;
using Capital.GSG.FX.FXConverterServiceConnector;
using Capital.GSG.FX.AzureTableConnector;
using Capital.GSG.FX.Data.Core.OrderData;
using static Capital.GSG.FX.Data.Core.OrderData.OrderSide;
using static Capital.GSG.FX.Data.Core.OrderData.TimeInForce;
using Capital.GSG.FX.Data.Core.AccountPortfolio;
using Capital.GSG.FX.Data.Core.ContractData;
using static Capital.GSG.FX.Data.Core.ContractData.Cross;
using Capital.GSG.FX.Trading.Executor.Core;
using Capital.GSG.FX.Data.Core.ExecutionData;
using Capital.GSG.FX.IBData.Service.Connector;
using Capital.GSG.FX.Data.Core.SystemData;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI.Executor
{
    class Program
    {
        private static ILog logger = LogManager.GetLogger(nameof(Program));

        private static IFxConverter fxConverter;
        private static PositionsConnector positionsConnector;
        private static ExecutionsConnector executionsConnector;
        private static OrdersConnector ordersConnector = null;
        private static MDConnector mdConnector = null;

        private static CancellationTokenSource stopRequestedCts = new CancellationTokenSource();

        private static object locker = new object();

        private static List<Position> latestPositionsUpdate = new List<Position>();

        internal static int RtBarsCounter = 0;

        private static ConcurrentDictionary<int, int> orderRequests = new ConcurrentDictionary<int, int>();

        static void Main(string[] args)
        {
            XmlConfigurator.Configure();

            GenericIBClientConfig brokerClientConfig = new GenericIBClientConfig()
            {
                ClientNumber = 7,
                Host = "tryphon.gsg.capital",
                IBDataServiceEndpoint = "https://tryphon.gsg.capital:6583",
                Name = "IB_MDClient_Test",
                Port = 7497,
                TradingAccount = "DU519219"
            };

            string monitoringEndpoint = "http://localhost:51468/";
            string convertServiceEndpoint = "https://tryphon.gsg.capital:6580";
            string marketDataServiceEndpoint = "https://tryphon.gsg.capital:6581";
            string azureTableConnectionString = "UseDevelopmentStorage=true;";

            logger.Info("Starting service IBExecutorTester");

            logger.Debug($"BrokerClientConfigId: {brokerClientConfig.Name}");
            logger.Debug($"MonitoringEndpoint: {monitoringEndpoint}");
            logger.Debug($"ConvertServiceEndpoint: {convertServiceEndpoint}");
            logger.Debug($"MDConnector: {marketDataServiceEndpoint}");

            fxConverter = ConvertConnector.GetConnector(convertServiceEndpoint);
            positionsConnector = PositionsConnector.GetConnector(monitoringEndpoint);
            executionsConnector = ExecutionsConnector.GetConnector(monitoringEndpoint);
            ordersConnector = OrdersConnector.GetConnector(monitoringEndpoint);
            mdConnector = MDConnector.GetConnector(marketDataServiceEndpoint);

            Do(brokerClientConfig, azureTableConnectionString).Wait();
        }

        private static async Task Do(GenericIBClientConfig brokerClientConfig, string azureTableConnectionString)
        {
            try
            {
                ContractsConnector contractsConnector = ContractsConnector.GetConnector("https://tryphon.gsg.capital:6583");

                List<Contract> ibContracts = await contractsConnector.GetAll();

                IBTestTradingExecutorRunner tradingExecutorRunner = new IBTestTradingExecutorRunner();

                AutoResetEvent stopCompleteEvent = new AutoResetEvent(false);

                IBrokerClient brokerClient = await BrokerClient.SetupBrokerClient(IBrokerClientType.Both, tradingExecutorRunner, brokerClientConfig, fxConverter, mdConnector, null, stopRequestedCts.Token, false, ibContracts);
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
                //positionUpdatePosterTimer = new Timer((state) =>
                //{
                //    if (latestPositionsUpdate?.Count() > 0 && !positionUpdatePosted)
                //    {
                //        foreach (Position position in latestPositionsUpdate)
                //            positionsConnector?.PostPosition(position, ct: stopRequestedCts.Token);

                //        latestPositionsUpdate = new List<Position>();

                //        lock (locker)
                //        {
                //            positionUpdatePosted = true;
                //        }
                //    }
                //}, null, 1000, 1000);

                await SubscribeAndListenRTBars(brokerClient);

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

                if ((await executor.CloseAllPositions(CrossUtils.AllCrosses)) != null)
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

            await Task.CompletedTask;

            //CancellationTokenSource cts = new CancellationTokenSource();
            //cts.CancelAfter(TimeSpan.FromSeconds(5));

            //await azureTableClient?.OrderActioner.AddOrUpdate(order, ct: cts.Token);

            //await ordersConnector?.PostOrder(order, ct: cts.Token);
        }

        private static async void PositionExecutor_AccountUpdated(Account account)
        {
            await Task.CompletedTask;

            Console.WriteLine("Received account update:");
            Console.WriteLine(account.ToString());

            //CancellationTokenSource cts = new CancellationTokenSource();
            //cts.CancelAfter(TimeSpan.FromSeconds(5));

            //await azureTableClient?.AccountActioner.AddOrUpdate(account, ct: cts.Token);
        }

        private static async void TradesExecutor_TradeReceived(Execution execution)
        {
            logger.Info($"Received execution of {CrossUtils.ToSring(execution.Cross)}: {execution}");

            await Task.CompletedTask;

            //CancellationTokenSource cts = new CancellationTokenSource();
            //cts.CancelAfter(TimeSpan.FromSeconds(5));

            //await azureTableClient?.ExecutionActioner.AddOrUpdate(execution, cts.Token);

            //await executionsConnector?.PostNewExecution(execution, ct: cts.Token);
        }

        private static async Task PlaceTrailingStopOrders(IOrderExecutor orderExecutor)
        {
            Order order1 = await orderExecutor.PlaceTrailingStopOrder(Cross.EURUSD, OrderSide.BUY, 20000, 0.001, DAY, "IBExecutorTester", ct: stopRequestedCts.Token);
            Console.WriteLine("TrailingStopOrder1: {0} ({1})", order1 != null ? "SUCCESS" : "FAILED", order1);
            Thread.Sleep(1000);

            Order order2 = await orderExecutor.PlaceTrailingStopOrder(Cross.EURUSD, OrderSide.SELL, 20000, 0.001, DAY, "IBExecutorTester", ct: stopRequestedCts.Token);
            Console.WriteLine("TrailingStopOrder2: {0} ({1})", order2 != null ? "SUCCESS" : "FAILED", order2);
            Thread.Sleep(1000);
        }

        private static async Task PlaceTrailingMarketIfTouchedOrders(IOrderExecutor orderExecutor)
        {
            Order order1 = await orderExecutor.PlaceTrailingMarketIfTouchedOrder(Cross.EURUSD, OrderSide.BUY, 20000, 0.001, DAY, "IBExecutorTester", ct: stopRequestedCts.Token);
            Console.WriteLine("TrailingMarketIfTouchedOrder1: {0} ({1})", order1 != null ? "SUCCESS" : "FAILED", order1);
            Thread.Sleep(1000);

            Order order2 = await orderExecutor.PlaceTrailingMarketIfTouchedOrder(Cross.EURUSD, OrderSide.SELL, 20000, 0.001, DAY, "IBExecutorTester", ct: stopRequestedCts.Token);
            Console.WriteLine("TrailingMarketIfTouchedOrder2: {0} ({1})", order2 != null ? "SUCCESS" : "FAILED", order2);
            Thread.Sleep(1000);
        }

        private static async Task PlaceMarketOrders(IOrderExecutor orderExecutor)
        {
            Order order1 = await orderExecutor.PlaceMarketOrder(EURUSD, OrderSide.SELL, 20000, DAY, "IBExecutorTester", ct: stopRequestedCts.Token);
            Console.WriteLine("MarketOrder1: {0} ({1})", order1 != null ? "SUCCESS" : "FAILED", order1);
            Thread.Sleep(1000);

            Order order2 = await orderExecutor.PlaceMarketOrder(EURCHF, OrderSide.SELL, 20000, DAY, "IBExecutorTester", ct: stopRequestedCts.Token);
            Console.WriteLine("MarketOrder2: {0} ({1})", order2 != null ? "SUCCESS" : "FAILED", order2);
            Thread.Sleep(1000);
        }

        private static async Task PlaceLimitOrders(IOrderExecutor orderExecutor)
        {
            Order order1 = await orderExecutor.PlaceLimitOrder(EURCHF, SELL, 20000, 1.125, DAY, "IBExecutorTester", ct: stopRequestedCts.Token);
            Console.WriteLine("LimitOrder1: {0} ({1})", order1 != null ? "SUCCESS" : "FAILED", order1);
            Thread.Sleep(1000);

            //Order order2 = await orderExecutor.PlaceLimitOrder(EURUSD, BUY, 20000, 1.115, DAY, "IBExecutorTester", ct: stopRequestedCts.Token);
            //Console.WriteLine("LimitOrder2: {0} ({1})", order2 != null ? "SUCCESS" : "FAILED", order2);
            //Thread.Sleep(1000);
        }

        private static async Task PlaceAndUpdateLimitOrders(IOrderExecutor orderExecutor)
        {
            Order order = await orderExecutor.PlaceLimitOrder(EURUSD, BUY, 20000, 1.115, DAY, "IBExecutorTester", ct: stopRequestedCts.Token);

            if (order != null)
            {
                Task.Delay(TimeSpan.FromSeconds(5)).Wait();
                await orderExecutor.UpdateOrderLevel(order.OrderID, 1.116);
            }

            Console.WriteLine("LimitOrder2: {0} ({1})", order != null ? "SUCCESS" : "FAILED", order);
            Task.Delay(TimeSpan.FromSeconds(1)).Wait();
        }

        private static async Task PlaceStopOrders(IOrderExecutor orderExecutor)
        {
            Order order1 = await orderExecutor.PlaceStopOrder(EURUSD, SELL, 20000, 1.115, DAY, "IBExecutorTester", ct: stopRequestedCts.Token);
            Console.WriteLine("StopOrder1: {0} ({1})", order1 != null ? "SUCCESS" : "FAILED", order1);
            Thread.Sleep(1000);

            Order order2 = await orderExecutor.PlaceStopOrder(EURUSD, BUY, 20000, 1.120, DAY, "IBExecutorTester", ct: stopRequestedCts.Token);
            Console.WriteLine("StopOrder2: {0} ({1})", order2 != null ? "SUCCESS" : "FAILED", order2);
            Thread.Sleep(1000);
        }

        private static async Task PlaceAndUpdateStopOrders(IOrderExecutor orderExecutor)
        {
            Order order = await orderExecutor.PlaceStopOrder(EURUSD, SELL, 20000, 1.115, DAY, "IBExecutorTester", ct: stopRequestedCts.Token);

            if (order != null)
            {
                Task.Delay(TimeSpan.FromSeconds(5)).Wait();
                await orderExecutor.UpdateOrderLevel(order.OrderID, 1.116);
            }

            Console.WriteLine("StopOrder1: {0} ({1})", order != null ? "SUCCESS" : "FAILED", order);
            Task.Delay(TimeSpan.FromSeconds(1)).Wait();
        }

        private static async void PositionExecutor_PositionsUpdateReceived(Position position)
        {
            logger.Info($"Received positions details:");

            await Task.CompletedTask;

            //try
            //{
            //    CancellationTokenSource cts = new CancellationTokenSource();
            //    cts.CancelAfter(TimeSpan.FromSeconds(5));

            //    Console.WriteLine(position);

            //    await azureTableClient?.PositionActioner.AddOrUpdate(position, cts.Token);

            //    lock (locker)
            //    {
            //        latestPositionsUpdate.Add(position);
            //        positionUpdatePosted = false;
            //    }
            //}
            //catch (OperationCanceledException)
            //{
            //    logger.Error("Failed to handle position update: operation cancelled");
            //}
            //catch (Exception ex)
            //{
            //    logger.Error("Failed to handle position update", ex);
            //}
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
