using Capital.GSG.FX.Data.Core.AccountPortfolio;
using Capital.GSG.FX.Data.Core.ContractData;
using Capital.GSG.FX.FXConverter;
using Capital.GSG.FX.Trading.Executor.Core;
using log4net;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI.Executor
{
    public class IBPositionsExecutor : IPositionExecutor
    {
        private static ILog logger = LogManager.GetLogger(nameof(IBPositionsExecutor));

        private readonly IFxConverter fxConverter;

        private readonly CancellationToken stopRequestedCt;

        private static IBPositionsExecutor _instance;

        private readonly IBClient ibClient;

        public ConcurrentDictionary<Tuple<string, Cross>, Position> Positions { get; } = new ConcurrentDictionary<Tuple<string, Cross>, Position>();

        public event Action<Position> PositionUpdated;

        public event Action<Account> AccountUpdated;

        private string accountSubscribed = null;

        private ConcurrentDictionary<string, Account> accounts = new ConcurrentDictionary<string, Account>();

        private IBPositionsExecutor(IBClient ibClient, IFxConverter fxConverter, string tradingAccount, CancellationToken stopRequestedCt)
        {
            if (fxConverter == null)
                throw new ArgumentNullException(nameof(fxConverter));

            this.fxConverter = fxConverter;

            this.stopRequestedCt = stopRequestedCt;

            this.ibClient = ibClient;

            // PortfolioUpdated actually returns position updates
            this.ibClient.ResponseManager.PortfolioUpdated += ResponseManager_PortfolioUpdated;

            this.ibClient.ResponseManager.AccountUpdateTimeReceived += ResponseManager_AccountUpdateTimeReceived;
            this.ibClient.ResponseManager.AccountValueUpdated += ResponseManager_AccountValueUpdated;

            this.ibClient.IBConnectionEstablished += () =>
            {
                logger.Info($"IB client (re)connected. Will resubcribe to account updates for {tradingAccount}");

                SubscribeAccountUpdates(tradingAccount);
            };
        }

        internal static IBPositionsExecutor SetupIBPositionsExecutor(IBClient ibClient, string tradingAccount, IFxConverter fxConverter, CancellationToken stopRequestedCt)
        {
            if (ibClient == null)
                throw new ArgumentNullException(nameof(ibClient));

            if (string.IsNullOrEmpty(tradingAccount))
                throw new ArgumentNullException(nameof(tradingAccount));

            _instance = new IBPositionsExecutor(ibClient, fxConverter, tradingAccount, stopRequestedCt);

            _instance.SubscribeAccountUpdates(tradingAccount);

            return _instance;
        }

        private void ResponseManager_PortfolioUpdated(Contract contract, int quantity, double marketPrice, double marketValue, double averageCost, double unrealisedPNL, double realisedPNL, string account)
        {
            Cross cross = contract.Cross;

            if (cross == Cross.UNKNOWN)
            {
                logger.Debug("Ignoring position update for UNKNOWN cross (might not be an FX position)");
                return;
            }

            logger.Debug($"Received position update for account {account}: {quantity} {cross} @ {averageCost}");

            double? realizedPnlUsd = ConvertToUsd(cross, realisedPNL);
            double? unrealizedPnlUsd = ConvertToUsd(cross, unrealisedPNL);

            Tuple<string, Cross> key = new Tuple<string, Cross>(account, cross);

            Position updatedPosition = new Position()
            {
                Account = account,
                AverageCost = averageCost,
                Broker = Broker.IB,
                Cross = cross,
                LastUpdate = DateTimeOffset.Now,
                MarketPrice = marketPrice,
                MarketValue = marketValue,
                PositionQuantity = quantity,
                RealizedPnL = realisedPNL,
                RealizedPnlUsd = realizedPnlUsd,
                UnrealizedPnL = unrealisedPNL,
                UnrealizedPnlUsd = unrealizedPnlUsd
            };

            Positions.AddOrUpdate(key, updatedPosition, (k, oldValue) => updatedPosition);

            PositionUpdated?.Invoke(updatedPosition);
        }

        private double? ConvertToUsd(Cross cross, double? pnl)
        {
            if (pnl.HasValue && pnl.Value != 0 && CrossUtils.GetQuotedCurrency(cross) != Currency.USD)
            {
                Task<double?> t = fxConverter.Convert(pnl.Value, CrossUtils.GetQuotedCurrency(cross), Currency.USD, stopRequestedCt);

                t.Wait();

                if (t.IsCompleted)
                    return t.Result;
            }

            return null;
        }

        private void ResponseManager_AccountValueUpdated(string attrKey, string attrValue, Currency currency, string accountName)
        {
            Account account = accounts.AddOrUpdate(accountSubscribed, new Account()
            {
                Attributes = new List<AccountAttribute>()
                {
                    new AccountAttribute() { Currency = currency, Key = attrKey, Value = attrValue }
                },
                Broker = Broker.IB,
                LastUpdate = DateTimeOffset.Now,
                Name = accountName
            }, (key, oldValue) =>
            {
                AccountAttribute existingAttribute = oldValue.Attributes.SingleOrDefault(attr => attr.Key == attrKey);

                if (existingAttribute == null)
                    oldValue.Attributes.Add(new AccountAttribute() { Currency = currency, Key = attrKey, Value = attrValue });
                else
                    existingAttribute.Value = attrValue;

                oldValue.LastUpdate = DateTimeOffset.Now;

                return oldValue;
            });

            AccountUpdated?.Invoke(account);
        }

        private void ResponseManager_AccountUpdateTimeReceived(DateTimeOffset lastUpdate)
        {
            if (!string.IsNullOrEmpty(accountSubscribed))
            {
                accounts.AddOrUpdate(accountSubscribed, new Account()
                {
                    Attributes = new List<AccountAttribute>(),
                    Broker = Broker.IB,
                    LastUpdate = lastUpdate,
                    Name = accountSubscribed
                }, (key, oldValue) =>
                {
                    oldValue.LastUpdate = lastUpdate;
                    return oldValue;
                });
            }
            else
                logger.Error("Received account last update time, but no account is currently subscribed to. Please check");
        }

        internal void SubscribeAccountUpdates(string accountName)
        {
            if (string.IsNullOrEmpty(accountName))
                throw new ArgumentNullException(nameof(accountName));

            if (!string.IsNullOrEmpty(accountSubscribed))
                UnsubcribeAccountUpdates();

            logger.Info($"Subscribe to account updates for {accountName}");

            ibClient.RequestManager.AccountRequestManager.SubscribeToAccountUpdates(accountName);

            accountSubscribed = accountName;
        }

        internal void UnsubcribeAccountUpdates()
        {
            if (!string.IsNullOrEmpty(accountSubscribed))
            {
                logger.Info($"Unsubscribe from account updates for {accountSubscribed}");

                ibClient.RequestManager.AccountRequestManager.UnsubscribeFromAccountUpdates(accountSubscribed);
                accountSubscribed = null;
            }
        }

        public void Dispose()
        {
            logger.Info("Disposing IBPositionsExecutor");

            UnsubcribeAccountUpdates();

            ibClient.RequestManager.AccountRequestManager.CancelRealTimePositionUpdates();
        }

        public double GetPositionQuantityForCross(Broker broker, string account, Cross cross)
        {
            Tuple<string, Cross> key = new Tuple<string, Cross>(account, cross);

            Position position;
            if (Positions.TryGetValue(key, out position))
                return position.PositionQuantity;
            else
                return 0;
        }

        public Dictionary<Cross, double> GetAllOpenPositions()
        {
            var positions = Positions.ToArray();

            return positions.ToDictionary(kvp => kvp.Key.Item2, kvp => kvp.Value.PositionQuantity);
        }
    }
}
