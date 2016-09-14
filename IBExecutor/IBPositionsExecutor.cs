using Capital.GSG.FX.FXConverter;
using Capital.GSG.FX.Trading.Executor;
using log4net;
using Net.Teirlinck.FX.Data.AccountPortfolio;
using Net.Teirlinck.FX.Data.AccountPortfolioData;
using Net.Teirlinck.FX.Data.ContractData;
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

        public ConcurrentDictionary<string, Position> Positions { get; } = new ConcurrentDictionary<string, Position>();

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
            Task.Run(async () =>
            {
                Cross cross = contract.Cross;

                if (cross == Cross.UNKNOWN)
                {
                    logger.Debug("Ignoring position update for UNKNOWN cross (might not be an FX position)");
                    return;
                }

                logger.Debug($"Received position update for account {account}: {quantity} {cross} @ {averageCost}");

                Position position = null;

                if (Positions.ContainsKey(account))
                {
                    position = Positions[account];

                    position.Timestamp = DateTime.Now;

                    PositionSecurity security = position.PositionSecurities.FirstOrDefault(p => p.Cross == contract.Cross);

                    if (security == null)
                    {
                        security = new PositionSecurity()
                        {
                            Cross = cross,
                            PositionQuantity = quantity,
                            AverageCost = averageCost,
                            MarketPrice = marketPrice,
                            MarketValue = marketValue,
                            RealizedPnL = realisedPNL,
                            UnrealizedPnL = unrealisedPNL
                        };

                        await ComputedUsdPnl(security);

                        position.PositionSecurities.Add(security);
                    }
                    else
                    {
                        security.PositionQuantity = quantity;
                        security.AverageCost = averageCost;
                        security.MarketPrice = marketPrice;
                        security.MarketValue = marketValue;
                        security.RealizedPnL = realisedPNL;
                        security.UnrealizedPnL = unrealisedPNL;

                        await ComputedUsdPnl(security);
                    }
                }
                else
                {
                    position = new Position(Broker.IB, account, DateTimeOffset.Now);

                    PositionSecurity security = new PositionSecurity()
                    {
                        Cross = cross,
                        PositionQuantity = quantity,
                        AverageCost = averageCost,
                        MarketPrice = marketPrice,
                        MarketValue = marketValue,
                        RealizedPnL = realisedPNL,
                        UnrealizedPnL = unrealisedPNL
                    };

                    await ComputedUsdPnl(security);

                    position.PositionSecurities.Add(security);
                }

                Positions.AddOrUpdate(account, position, (key, oldValue) => position);

                if (Positions.Count > 0)
                    PositionUpdated?.Invoke(position);
            });
        }

        private async Task ComputedUsdPnl(PositionSecurity posSecurity)
        {
            // USD Unrealized PnL
            if (posSecurity.UnrealizedPnL.HasValue && posSecurity.UnrealizedPnL.Value != 0)
            {
                if (CrossUtils.GetQuotedCurrency(posSecurity.Cross) == Currency.USD)
                    posSecurity.UnrealizedPnlUsd = posSecurity.UnrealizedPnL.Value;
                else
                    posSecurity.UnrealizedPnlUsd = await fxConverter.Convert(posSecurity.UnrealizedPnL.Value, CrossUtils.GetQuotedCurrency(posSecurity.Cross), Currency.USD, stopRequestedCt);

                logger.Debug($"Computed unrealised USD PnL for {posSecurity.Cross} position: {posSecurity.UnrealizedPnlUsd} USD");
            }

            // USD Realized PnL
            if (posSecurity.RealizedPnL.HasValue && posSecurity.RealizedPnL.Value != 0)
            {
                if (CrossUtils.GetQuotedCurrency(posSecurity.Cross) == Currency.USD)
                    posSecurity.RealizedPnlUsd = posSecurity.RealizedPnL.Value;
                else
                    posSecurity.RealizedPnlUsd = await fxConverter.Convert(posSecurity.RealizedPnL.Value, CrossUtils.GetQuotedCurrency(posSecurity.Cross), Currency.USD, stopRequestedCt);

                logger.Debug($"Computed realised USD PnL for {posSecurity.Cross} position: {posSecurity.UnrealizedPnlUsd} USD");
            }
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

        public double GetPositionQuantityForCross(Cross cross, string account)
        {
            if (Positions.ContainsKey(account))
                return Positions[account].PositionSecurities.Where(ps => ps.Cross == cross).FirstOrDefault()?.PositionQuantity ?? 0.0;
            else
                return 0.0;
        }

        public Dictionary<Cross, double> GetAllOpenPositions()
        {
            Dictionary<Cross, double> openPositions = new Dictionary<Cross, double>();

            // Take a snapshot of the dictionary
            var positions = Positions.Values;

            foreach (var position in positions)
            {
                foreach (var posSecurity in position.PositionSecurities)
                {
                    if (!openPositions.ContainsKey(posSecurity.Cross))
                    {
                        if (posSecurity.PositionQuantity != 0)
                            openPositions.Add(posSecurity.Cross, posSecurity.PositionQuantity);
                    }
                    else
                    {
                        if (posSecurity.PositionQuantity != 0)
                        {
                            double newQty = openPositions[posSecurity.Cross] + posSecurity.PositionQuantity;

                            if (newQty != 0)
                                openPositions[posSecurity.Cross] = newQty;
                            else
                                openPositions.Remove(posSecurity.Cross);
                        }
                    }
                }
            }

            return openPositions;
        }

        private class PositionInternal
        {

        }
    }
}
