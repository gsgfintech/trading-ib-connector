using Capital.GSG.FX.Trading.Executor;
using log4net;
using Net.Teirlinck.FX.Data.ContractData;
using Net.Teirlinck.FX.Data.ExecutionData;
using Net.Teirlinck.FX.Data.System;
using static Net.Teirlinck.FX.Data.System.AlertLevel;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Net.Teirlinck.FX.Data.OrderData;
using Capital.GSG.FX.FXConverter;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI.Executor
{
    public class IBTradesExecutor : ITradesExecutor
    {
        private static ILog logger = LogManager.GetLogger(nameof(IBTradesExecutor));

        private IBClient IBClient { get; set; }
        private readonly BrokerClient brokerClient;
        private readonly IFxConverter fxConverter;

        private readonly CancellationToken stopRequestedCt;

        public List<Execution> Trades { get; } = new List<Execution>();

        private ConcurrentDictionary<string, TempExecution> tmpExecutions = new ConcurrentDictionary<string, TempExecution>();
        private ConcurrentDictionary<int, int> partialExecutions = new ConcurrentDictionary<int, int>();

        public event Action<Execution> TradeReceived;

        internal IBTradesExecutor(BrokerClient brokerClient, IBClient ibClient, IFxConverter fxConverter, CancellationToken stopRequestedCt)
        {
            if (ibClient == null)
                throw new ArgumentNullException(nameof(ibClient));

            if (brokerClient == null)
                throw new ArgumentNullException(nameof(brokerClient));

            if (fxConverter == null)
                throw new ArgumentNullException(nameof(fxConverter));

            this.brokerClient = brokerClient;

            this.fxConverter = fxConverter;

            this.stopRequestedCt = stopRequestedCt;

            IBClient = ibClient;
            IBClient.ResponseManager.ExecutionDetailsReceived += ResponseManager_ExecutionDetailsReceived;
            IBClient.ResponseManager.CommissionReportReceived += ResponseManager_CommissionReportReceived;

            IBClient.IBConnectionEstablished += () =>
            {
                logger.Info("IB client (re)connected. Will re-request today's executions");
            };
        }

        private async void ResponseManager_CommissionReportReceived(CommissionReport report)
        {
            logger.Debug($"Received commission report for trade {report.ExecutionID}");

            TempExecution tmpExecution = tmpExecutions.GetOrAdd(report.ExecutionID, new TempExecution() { CommissionReport = report });

            if (tmpExecution.Execution != null) // ready to send out
            {
                Execution execution = tmpExecution.Execution;
                execution.Commission = report.Commission;
                execution.CommissionCcy = report.Currency;
                execution.RealizedPnL = report.RealizedPnL;

                // USD Commission
                if (execution.Commission.HasValue && execution.CommissionCcy.HasValue)
                {
                    if (execution.CommissionCcy.Value == Currency.USD)
                        execution.CommissionUsd = execution.Commission.Value;
                    else
                        execution.CommissionUsd = await fxConverter.Convert(execution.Commission.Value, execution.CommissionCcy.Value, Currency.USD, stopRequestedCt);

                    logger.Debug($"Computed USD commission for trade {execution.Id}: {execution.CommissionUsd} USD");
                }

                // USD Realized PnL
                if (execution.RealizedPnL.HasValue)
                {
                    if (CrossUtils.GetQuotedCurrency(execution.Cross) == Currency.USD)
                        execution.RealizedPnlUsd = execution.RealizedPnL;
                    else
                        execution.RealizedPnlUsd = await fxConverter.Convert(execution.RealizedPnL.Value, CrossUtils.GetQuotedCurrency(execution.Cross), Currency.USD, stopRequestedCt);

                    logger.Debug($"Computed USD PnL for trade {execution.Id}: {execution.RealizedPnlUsd} USD");

                    execution.RealizedPnlPips = CrossUtils.ConvertToFractionalPips(execution.RealizedPnL.Value / (double)execution.Quantity, execution.Cross);

                    DateTime? previousTrade = GetPreviousExecutionTimeForCross(execution.Cross);
                    if (previousTrade.HasValue)
                        execution.TradeDuration = execution.ExecutionTime.Subtract(previousTrade.Value).ToString();
                }

                Trades.Add(execution);
                brokerClient.UpdateStatus("TradesCount", Trades.Count, SystemStatusLevel.GREEN);
                TradeReceived?.Invoke(execution);

                // Remove the key from the dictionary
                TempExecution discarded;
                tmpExecutions.TryRemove(execution.Id, out discarded);
            }
        }

        private void EnrichWithOrderData(ref Execution execution)
        {
            Order order = (brokerClient.OrderExecutor as IBOrderExecutor)?.GetOrder(execution.OrderId);

            execution.OrderOrigin = order?.Origin ?? OrderOrigin.Unknown;
        }

        private DateTime? GetPreviousExecutionTimeForCross(Cross cross)
        {
            return Trades?.LastOrDefault(exec => exec.Cross == cross)?.ExecutionTime;
        }

        private async void ResponseManager_ExecutionDetailsReceived(int reqId, Contract contract, Execution execution)
        {
            if (execution?.ClientId != IBClient.ClientID)
            {
                logger.Debug($"Ignoring execution {execution?.ClientId} message as it was placed by a different IBClient (execution's client: {execution?.ClientId}, this client: {IBClient.ClientID})");
                return;
            }

            logger.Info($"Received new trade notification: {execution}");

            execution.Cross = contract.Cross;

            EnrichWithOrderData(ref execution);

            TempExecution tmpExecution = tmpExecutions.GetOrAdd(execution.Id, new TempExecution() { Execution = execution });

            int newCumulativeQuantity = execution.Quantity;

            int previousCumulativeQuantity;
            if (partialExecutions.TryGetValue(execution.OrderId, out previousCumulativeQuantity))
            {
                execution.Quantity -= previousCumulativeQuantity; // IB sends execution details with cumulative quantities, which override any quantity previously executed already

                logger.Info($"Received an additional execution for order {execution.OrderId}. The previous execution(s) were therefore partial. The cumulative quantity is {newCumulativeQuantity} and this execution's quantity is {execution.Quantity}");
            }

            // Keep track of the cumulative quantity already executed for that order
            partialExecutions.AddOrUpdate(execution.OrderId, newCumulativeQuantity, (key, oldVal) => newCumulativeQuantity);

            if (tmpExecution.CommissionReport != null) // ready to send out
            {
                execution.Commission = tmpExecution.CommissionReport.Commission;
                execution.CommissionCcy = tmpExecution.CommissionReport.Currency;

                // USD Commission
                if (execution.Commission.HasValue && execution.CommissionCcy.HasValue)
                {
                    if (execution.CommissionCcy.Value == Currency.USD)
                        execution.CommissionUsd = execution.Commission.Value;
                    else
                        execution.CommissionUsd = await fxConverter.Convert(execution.Commission.Value, execution.CommissionCcy.Value, Currency.USD, stopRequestedCt);

                    logger.Debug($"Computed USD commission for trade {execution.Id}: {execution.CommissionUsd} USD");
                }

                // USD Realized PnL
                if (execution.RealizedPnL.HasValue)
                {
                    if (CrossUtils.GetQuotedCurrency(execution.Cross) == Currency.USD)
                        execution.RealizedPnlUsd = execution.RealizedPnL;
                    else if (CrossUtils.GetBaseCurrency(execution.Cross) == Currency.USD)
                        execution.RealizedPnlUsd = 1 / execution.RealizedPnL;
                    else
                        execution.RealizedPnlUsd = await fxConverter.Convert(execution.RealizedPnL.Value, CrossUtils.GetQuotedCurrency(execution.Cross), Currency.USD, stopRequestedCt);

                    logger.Debug($"Computed USD PnL for trade {execution.Id}: {execution.RealizedPnlUsd} USD");
                }

                Trades.Add(execution);
                brokerClient.UpdateStatus("TradesCount", Trades.Count, SystemStatusLevel.GREEN);
                TradeReceived?.Invoke(execution);

                // Remove the key from the dictionary
                TempExecution discarded;
                tmpExecutions.TryRemove(execution.Id, out discarded);
            }
        }

        private void SendAlertError(string subject, string body)
        {
            brokerClient.OnAlert(new Alert(ERROR, "IBTradeExecutor", subject, body));
        }

        public void Dispose()
        {
            logger.Info("Disposing IBTradesExecutor");
        }

        private class TempExecution
        {
            public Execution Execution { get; set; } = null;

            public CommissionReport CommissionReport { get; set; } = null;
        }

        private class PartialExecution
        {
            public int CumulativeQuantity { get; set; }
            public int CumulativeCommission { get; set; }
        }
    }
}
