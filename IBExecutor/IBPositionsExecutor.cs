using Capital.GSG.FX.Data.Core.AccountPortfolio;
using Capital.GSG.FX.Data.Core.ContractData;
using Capital.GSG.FX.FXConverter;
using Capital.GSG.FX.Trading.Executor.Core;
using Capital.GSG.FX.Utils.Core;
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

        //private ConcurrentDictionary<string, Account> accounts = new ConcurrentDictionary<string, Account>();

        private IBPositionsExecutor(IBClient ibClient, IFxConverter fxConverter, string tradingAccount, CancellationToken stopRequestedCt)
        {
            if (fxConverter == null)
                throw new ArgumentNullException(nameof(fxConverter));

            this.fxConverter = fxConverter;

            this.stopRequestedCt = stopRequestedCt;

            this.ibClient = ibClient;

            // PortfolioUpdated actually returns position updates
            this.ibClient.ResponseManager.PortfolioUpdated += ResponseManager_PortfolioUpdated;

            //this.ibClient.ResponseManager.AccountUpdateTimeReceived += ResponseManager_AccountUpdateTimeReceived;
            //this.ibClient.ResponseManager.AccountValueUpdated += ResponseManager_AccountValueUpdated;

            this.ibClient.IBConnectionEstablished += () =>
            {
                logger.Info($"IB client (re)connected. Will resubcribe to account updates for {tradingAccount}");

                //SubscribeAccountUpdates(tradingAccount);
            };
        }

        internal static IBPositionsExecutor SetupIBPositionsExecutor(IBClient ibClient, string tradingAccount, IFxConverter fxConverter, CancellationToken stopRequestedCt)
        {
            if (ibClient == null)
                throw new ArgumentNullException(nameof(ibClient));

            if (string.IsNullOrEmpty(tradingAccount))
                throw new ArgumentNullException(nameof(tradingAccount));

            _instance = new IBPositionsExecutor(ibClient, fxConverter, tradingAccount, stopRequestedCt);

            //_instance.SubscribeAccountUpdates(tradingAccount);

            return _instance;
        }

        private void ResponseManager_PortfolioUpdated(Contract contract, double quantity, double marketPrice, double marketValue, double averageCost, double unrealisedPNL, double realisedPNL, string account)
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

        //private void ResponseManager_AccountValueUpdated(string attrKey, string attrValue, Currency currency, string accountName)
        //{
        //    Account account = accounts.AddOrUpdate(accountSubscribed, new Account()
        //    {
        //        Attributes = new List<AccountAttribute>()
        //        {
        //            new AccountAttribute() { Currency = currency, Key = attrKey, Value = attrValue }
        //        },
        //        Broker = Broker.IB,
        //        LastUpdate = DateTimeOffset.Now,
        //        Name = accountName
        //    }, (key, oldValue) =>
        //    {
        //        AccountAttribute existingAttribute = oldValue.Attributes.SingleOrDefault(attr => attr.Key == attrKey);

        //        if (existingAttribute == null)
        //            oldValue.Attributes.Add(new AccountAttribute() { Currency = currency, Key = attrKey, Value = attrValue });
        //        else
        //            existingAttribute.Value = attrValue;

        //        oldValue.LastUpdate = DateTimeOffset.Now;

        //        return oldValue;
        //    });

        //    AccountUpdated?.Invoke(account);
        //}

        //private void ResponseManager_AccountUpdateTimeReceived(DateTimeOffset lastUpdate)
        //{
        //    if (!string.IsNullOrEmpty(accountSubscribed))
        //    {
        //        accounts.AddOrUpdate(accountSubscribed, new Account()
        //        {
        //            Attributes = new List<AccountAttribute>(),
        //            Broker = Broker.IB,
        //            LastUpdate = lastUpdate,
        //            Name = accountSubscribed
        //        }, (key, oldValue) =>
        //        {
        //            oldValue.LastUpdate = lastUpdate;
        //            return oldValue;
        //        });
        //    }
        //    else
        //        logger.Error("Received account last update time, but no account is currently subscribed to. Please check");
        //}

        internal void SubscribeAccountUpdates(string accountName)
        {
            if (string.IsNullOrEmpty(accountName))
                throw new ArgumentNullException(nameof(accountName));

            if (!string.IsNullOrEmpty(accountSubscribed))
                UnsubcribeAccountUpdates();

            logger.Info($"Subscribe to account updates for {accountName}");

            ibClient.RequestManager.AccountRequestManager.SubscribeToAccountUpdates(accountName);
            ibClient.RequestManager.AccountRequestManager.RequestAllPositions();

            accountSubscribed = accountName;
        }

        internal void UnsubcribeAccountUpdates()
        {
            if (!string.IsNullOrEmpty(accountSubscribed))
            {
                logger.Info($"Unsubscribe from account updates for {accountSubscribed}");

                ibClient.RequestManager.AccountRequestManager.UnsubscribeFromAccountUpdates(accountSubscribed);
                ibClient.RequestManager.AccountRequestManager.CancelRealTimePositionUpdates();

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

        #region Request Account Details
        private AutoResetEvent accountDetailsReceivedEvent;
        private bool isRequestingAccountDetails;
        private object isRequestingAccountDetailsLocker = new object();

        private string activeAccount;
        private Dictionary<string, Account> accounts;

        public async Task<(bool Success, string Message, Dictionary<string, Account> Accounts)> RequestAccountsDetails(IEnumerable<string> accountNames)
        {
            if (accountNames.IsNullOrEmpty())
                return (false, $"Unable to request accounts details: {nameof(accountNames)} is null or empty", null);

            if (isRequestingAccountDetails)
                return (false, "Unable to request accounts details: another request is already in progress. Try again later", null);

            SetIsRequestingAccountDetailsFlag(true);

            return await Task.Run(() =>
            {
                try
                {
                    stopRequestedCt.ThrowIfCancellationRequested();

                    accounts = new Dictionary<string, Account>();

                    ibClient.ResponseManager.AccountValueUpdated += AccountDetailsAttributeReceived;
                    ibClient.ResponseManager.AccountUpdateTimeReceived += AccountUpdateTimeReceived;
                    ibClient.ResponseManager.AccountDownloaded += AccountDetailsEnded;

                    bool received = true;

                    foreach (var accountName in accountNames)
                    {
                        if (string.IsNullOrEmpty(accountName))
                            continue;

                        accountDetailsReceivedEvent = new AutoResetEvent(false);

                        activeAccount = accountName;
                        accounts.Add(accountName, new Account()
                        {
                            Broker = Broker.IB,
                            Name = accountName
                        });

                        ibClient.RequestManager.AccountRequestManager.SubscribeToAccountUpdates(accountName);

                        received = accountDetailsReceivedEvent.WaitOne(TimeSpan.FromSeconds(3)) && received;

                        if (accounts[accountName].Cushion.Currency == Currency.UNKNOWN)
                            accounts[accountName].Cushion.Currency = accounts[accountName].BaseCurrency;

                        ibClient.RequestManager.AccountRequestManager.UnsubscribeFromAccountUpdates(accountName);

                        accountDetailsReceivedEvent.Reset();

                        Task.Delay(TimeSpan.FromSeconds(1));
                    }

                    ibClient.ResponseManager.AccountValueUpdated -= AccountDetailsAttributeReceived;
                    ibClient.ResponseManager.AccountUpdateTimeReceived -= AccountUpdateTimeReceived;
                    ibClient.ResponseManager.AccountDownloaded -= AccountDetailsEnded;

                    if (received)
                        return (true, "Received accounts details", accounts);
                    else
                        return (false, "Didn't receive any account details in the specified timeframe of 3 seconds", null);
                }
                catch (OperationCanceledException)
                {
                    string err = "Not requesting accounts details: operation cancelled";
                    logger.Error(err);
                    return (false, err, null);
                }
                catch (Exception ex)
                {
                    string err = "Failed to request accounts details";
                    logger.Error(err, ex);
                    return (false, $"{err}: {ex.Message}", null);
                }
                finally
                {
                    SetIsRequestingAccountDetailsFlag(false);
                }
            }, stopRequestedCt);
        }

        private void SetIsRequestingAccountDetailsFlag(bool value)
        {
            lock (isRequestingAccountDetailsLocker)
            {
                accounts = null;
                isRequestingAccountDetails = value;
            }
        }

        private void AccountDetailsAttributeReceived(string key, string strValue, Currency currency, string accountName)
        {
            if (accounts.ContainsKey(accountName))
            {
                try
                {
                    switch (key)
                    {
                        case "AccountType":
                            accounts[accountName].AccountType = strValue;
                            break;
                        case "AccruedCash":
                            double accruedCash;
                            if (double.TryParse(strValue, out accruedCash))
                                accounts[accountName].AccruedCash.TotalValue = accruedCash;
                            accounts[accountName].AccruedCash.Currency = currency;
                            break;
                        case "AccruedCash-C":
                            double accruedCashC;
                            if (double.TryParse(strValue, out accruedCashC))
                                accounts[accountName].AccruedCash.CommoditiesValue = accruedCashC;
                            accounts[accountName].AccruedCash.Currency = currency;
                            break;
                        case "AccruedCash-S":
                            double accruedCashS;
                            if (double.TryParse(strValue, out accruedCashS))
                                accounts[accountName].AccruedCash.StocksValue = accruedCashS;
                            accounts[accountName].AccruedCash.Currency = currency;
                            break;
                        case "AccruedDividend":
                            double accruedDividend;
                            if (double.TryParse(strValue, out accruedDividend))
                                accounts[accountName].AccruedDividend.TotalValue = accruedDividend;
                            accounts[accountName].AccruedDividend.Currency = currency;
                            break;
                        case "AccruedDividend-C":
                            double accruedDividendC;
                            if (double.TryParse(strValue, out accruedDividendC))
                                accounts[accountName].AccruedDividend.CommoditiesValue = accruedDividendC;
                            accounts[accountName].AccruedDividend.Currency = currency;
                            break;
                        case "AccruedDividend-S":
                            double accruedDividendS;
                            if (double.TryParse(strValue, out accruedDividendS))
                                accounts[accountName].AccruedDividend.StocksValue = accruedDividendS;
                            accounts[accountName].AccruedDividend.Currency = currency;
                            break;
                        case "AvailableFunds":
                            double availableFunds;
                            if (double.TryParse(strValue, out availableFunds))
                                accounts[accountName].AvailableFunds.TotalValue = availableFunds;
                            accounts[accountName].AvailableFunds.Currency = currency;
                            break;
                        case "AvailableFunds-C":
                            double availableFundsC;
                            if (double.TryParse(strValue, out availableFundsC))
                                accounts[accountName].AvailableFunds.CommoditiesValue = availableFundsC;
                            accounts[accountName].AvailableFunds.Currency = currency;
                            break;
                        case "AvailableFunds-S":
                            double availableFundsS;
                            if (double.TryParse(strValue, out availableFundsS))
                                accounts[accountName].AvailableFunds.StocksValue = availableFundsS;
                            accounts[accountName].AvailableFunds.Currency = currency;
                            break;
                        case "Billable":
                            double billable;
                            if (double.TryParse(strValue, out billable))
                                accounts[accountName].Billable.TotalValue = billable;
                            accounts[accountName].Billable.Currency = currency;
                            break;
                        case "Billable-C":
                            double billableC;
                            if (double.TryParse(strValue, out billableC))
                                accounts[accountName].Billable.CommoditiesValue = billableC;
                            accounts[accountName].Billable.Currency = currency;
                            break;
                        case "Billable-S":
                            double billableS;
                            if (double.TryParse(strValue, out billableS))
                                accounts[accountName].Billable.StocksValue = billableS;
                            accounts[accountName].Billable.Currency = currency;
                            break;
                        case "BuyingPower":
                            double buyingPower;
                            if (double.TryParse(strValue, out buyingPower))
                                accounts[accountName].BuyingPower.Value = buyingPower;
                            accounts[accountName].BuyingPower.Currency = currency;
                            break;
                        case "CashBalance":
                            double cashBalance;
                            if (double.TryParse(strValue, out cashBalance))
                            {
                                if (currency == Currency.USD)
                                    accounts[accountName].CashBalance.UsdValue = cashBalance;
                                else
                                    accounts[accountName].CashBalance.BaseCurrencyValue = cashBalance;
                            }
                            break;
                        case "CorporateBondValue":
                            double corporateBondValue;
                            if (double.TryParse(strValue, out corporateBondValue))
                            {
                                if (currency == Currency.USD)
                                    accounts[accountName].CorporateBondValue.UsdValue = corporateBondValue;
                                else
                                    accounts[accountName].CorporateBondValue.BaseCurrencyValue = corporateBondValue;
                            }
                            break;
                        case "Cushion":
                            double cushion;
                            if (double.TryParse(strValue, out cushion))
                                accounts[accountName].Cushion.Value = cushion;
                            accounts[accountName].Cushion.Currency = currency;
                            break;
                        case "EquityWithLoanValue":
                            double equityWithLoanValue;
                            if (double.TryParse(strValue, out equityWithLoanValue))
                                accounts[accountName].EquityWithLoanValue.TotalValue = equityWithLoanValue;
                            accounts[accountName].EquityWithLoanValue.Currency = currency;
                            break;
                        case "EquityWithLoanValue-C":
                            double equityWithLoanValueC;
                            if (double.TryParse(strValue, out equityWithLoanValueC))
                                accounts[accountName].EquityWithLoanValue.CommoditiesValue = equityWithLoanValueC;
                            accounts[accountName].EquityWithLoanValue.Currency = currency;
                            break;
                        case "EquityWithLoanValue-S":
                            double equityWithLoanValueS;
                            if (double.TryParse(strValue, out equityWithLoanValueS))
                                accounts[accountName].EquityWithLoanValue.StocksValue = equityWithLoanValueS;
                            break;
                        case "ExcessLiquidity":
                            double excessLiquidity;
                            if (double.TryParse(strValue, out excessLiquidity))
                                accounts[accountName].ExcessLiquidity.TotalValue = excessLiquidity;
                            accounts[accountName].ExcessLiquidity.Currency = currency;
                            break;
                        case "ExcessLiquidity-C":
                            double excessLiquidityC;
                            if (double.TryParse(strValue, out excessLiquidityC))
                                accounts[accountName].ExcessLiquidity.CommoditiesValue = excessLiquidityC;
                            accounts[accountName].ExcessLiquidity.Currency = currency;
                            break;
                        case "ExcessLiquidity-S":
                            double excessLiquidityS;
                            if (double.TryParse(strValue, out excessLiquidityS))
                                accounts[accountName].ExcessLiquidity.StocksValue = excessLiquidityS;
                            accounts[accountName].ExcessLiquidity.Currency = currency;
                            break;
                        case "ExchangeRate":
                            double exchangeRate;
                            if (double.TryParse(strValue, out exchangeRate))
                            {
                                if (currency == Currency.USD)
                                    accounts[accountName].ExchangeRate.UsdValue = exchangeRate;
                                else
                                    accounts[accountName].ExchangeRate.BaseCurrencyValue = exchangeRate;
                            }
                            break;
                        case "FullAvailableFunds":
                            double fullAvailableFunds;
                            if (double.TryParse(strValue, out fullAvailableFunds))
                                accounts[accountName].FullAvailableFunds.TotalValue = fullAvailableFunds;
                            accounts[accountName].FullAvailableFunds.Currency = currency;
                            break;
                        case "FullAvailableFunds-C":
                            double fullAvailableFundsC;
                            if (double.TryParse(strValue, out fullAvailableFundsC))
                                accounts[accountName].FullAvailableFunds.CommoditiesValue = fullAvailableFundsC;
                            accounts[accountName].FullAvailableFunds.Currency = currency;
                            break;
                        case "FullAvailableFunds-S":
                            double fullAvailableFundsS;
                            if (double.TryParse(strValue, out fullAvailableFundsS))
                                accounts[accountName].FullAvailableFunds.StocksValue = fullAvailableFundsS;
                            accounts[accountName].FullAvailableFunds.Currency = currency;
                            break;
                        case "FullExcessLiquidity":
                            double fullExcessLiquidity;
                            if (double.TryParse(strValue, out fullExcessLiquidity))
                                accounts[accountName].FullExcessLiquidity.TotalValue = fullExcessLiquidity;
                            accounts[accountName].FullExcessLiquidity.Currency = currency;
                            break;
                        case "FullExcessLiquidity-C":
                            double fullExcessLiquidityC;
                            if (double.TryParse(strValue, out fullExcessLiquidityC))
                                accounts[accountName].FullExcessLiquidity.CommoditiesValue = fullExcessLiquidityC;
                            accounts[accountName].FullExcessLiquidity.Currency = currency;
                            break;
                        case "FullExcessLiquidity-S":
                            double fullExcessLiquidityS;
                            if (double.TryParse(strValue, out fullExcessLiquidityS))
                                accounts[accountName].FullExcessLiquidity.StocksValue = fullExcessLiquidityS;
                            accounts[accountName].FullExcessLiquidity.Currency = currency;
                            break;
                        case "FullInitMarginReq":
                            double fullInitMarginReq;
                            if (double.TryParse(strValue, out fullInitMarginReq))
                                accounts[accountName].FullInitMarginReq.TotalValue = fullInitMarginReq;
                            accounts[accountName].FullInitMarginReq.Currency = currency;
                            break;
                        case "FullInitMarginReq-C":
                            double fullInitMarginReqC;
                            if (double.TryParse(strValue, out fullInitMarginReqC))
                                accounts[accountName].FullInitMarginReq.CommoditiesValue = fullInitMarginReqC;
                            accounts[accountName].FullInitMarginReq.Currency = currency;
                            break;
                        case "FullInitMarginReq-S":
                            double fullInitMarginReqS;
                            if (double.TryParse(strValue, out fullInitMarginReqS))
                                accounts[accountName].FullInitMarginReq.StocksValue = fullInitMarginReqS;
                            accounts[accountName].FullInitMarginReq.Currency = currency;
                            break;
                        case "FullMaintMarginReq":
                            double fullMaintMarginReq;
                            if (double.TryParse(strValue, out fullMaintMarginReq))
                                accounts[accountName].FullMaintMarginReq.TotalValue = fullMaintMarginReq;
                            accounts[accountName].FullMaintMarginReq.Currency = currency;
                            break;
                        case "FullMaintMarginReq-C":
                            double fullMaintMarginReqC;
                            if (double.TryParse(strValue, out fullMaintMarginReqC))
                                accounts[accountName].FullMaintMarginReq.CommoditiesValue = fullMaintMarginReqC;
                            break;
                        case "FullMaintMarginReq-S":
                            double fullMaintMarginReqS;
                            if (double.TryParse(strValue, out fullMaintMarginReqS))
                                accounts[accountName].FullMaintMarginReq.StocksValue = fullMaintMarginReqS;
                            accounts[accountName].FullMaintMarginReq.Currency = currency;
                            break;
                        case "FundValue":
                            double fundValue;
                            if (double.TryParse(strValue, out fundValue))
                            {
                                if (currency == Currency.USD)
                                    accounts[accountName].FundValue.UsdValue = fundValue;
                                else
                                    accounts[accountName].FundValue.BaseCurrencyValue = fundValue;
                            }
                            break;
                        case "FutureOptionValue":
                            double futureOptionValue;
                            if (double.TryParse(strValue, out futureOptionValue))
                            {
                                if (currency == Currency.USD)
                                    accounts[accountName].FutureOptionValue.UsdValue = futureOptionValue;
                                else
                                    accounts[accountName].FutureOptionValue.BaseCurrencyValue = futureOptionValue;
                            }
                            break;
                        case "FuturesPNL":
                            double futuresPnl;
                            if (double.TryParse(strValue, out futuresPnl))
                            {
                                if (currency == Currency.USD)
                                    accounts[accountName].FuturesPNL.UsdValue = futuresPnl;
                                else
                                    accounts[accountName].FuturesPNL.BaseCurrencyValue = futuresPnl;
                            }
                            break;
                        case "FxCashBalance":
                            double fxCashBalance;
                            if (double.TryParse(strValue, out fxCashBalance))
                            {
                                if (currency == Currency.USD)
                                    accounts[accountName].FxCashBalance.UsdValue = fxCashBalance;
                                else
                                    accounts[accountName].FxCashBalance.BaseCurrencyValue = fxCashBalance;
                            }
                            break;
                        case "GrossPositionValue":
                            double grossPositionValue;
                            if (double.TryParse(strValue, out grossPositionValue))
                                accounts[accountName].GrossPositionValue.TotalValue = grossPositionValue;
                            accounts[accountName].GrossPositionValue.Currency = currency;
                            break;
                        case "GrossPositionValue-C":
                            double grossPositionValueC;
                            if (double.TryParse(strValue, out grossPositionValueC))
                                accounts[accountName].GrossPositionValue.Currency = currency;
                            accounts[accountName].GrossPositionValue.CommoditiesValue = grossPositionValueC;
                            break;
                        case "GrossPositionValue-S":
                            double grossPositionValueS;
                            if (double.TryParse(strValue, out grossPositionValueS))
                                accounts[accountName].GrossPositionValue.StocksValue = grossPositionValueS;
                            accounts[accountName].GrossPositionValue.Currency = currency;
                            break;
                        case "IndianStockHaircut":
                            double indianStockHaircut;
                            if (double.TryParse(strValue, out indianStockHaircut))
                                accounts[accountName].IndianStockHaircut.TotalValue = indianStockHaircut;
                            accounts[accountName].IndianStockHaircut.Currency = currency;
                            break;
                        case "IndianStockHaircut-C":
                            double indianStockHaircutC;
                            if (double.TryParse(strValue, out indianStockHaircutC))
                                accounts[accountName].IndianStockHaircut.CommoditiesValue = indianStockHaircutC;
                            break;
                        case "IndianStockHaircut-S":
                            double indianStockHaircutS;
                            if (double.TryParse(strValue, out indianStockHaircutS))
                                accounts[accountName].IndianStockHaircut.StocksValue = indianStockHaircutS;
                            accounts[accountName].IndianStockHaircut.Currency = currency;
                            break;
                        case "InitMarginReq":
                            double initMarginReq;
                            if (double.TryParse(strValue, out initMarginReq))
                                accounts[accountName].InitMarginReq.TotalValue = initMarginReq;
                            accounts[accountName].InitMarginReq.Currency = currency;
                            break;
                        case "InitMarginReq-C":
                            double initMarginReqC;
                            if (double.TryParse(strValue, out initMarginReqC))
                                accounts[accountName].InitMarginReq.CommoditiesValue = initMarginReqC;
                            accounts[accountName].InitMarginReq.Currency = currency;
                            break;
                        case "InitMarginReq-S":
                            double initMarginReqS;
                            if (double.TryParse(strValue, out initMarginReqS))
                                accounts[accountName].InitMarginReq.StocksValue = initMarginReqS;
                            accounts[accountName].InitMarginReq.Currency = currency;
                            break;
                        case "IssuerOptionValue":
                            double issuerOptionValue;
                            if (double.TryParse(strValue, out issuerOptionValue))
                            {
                                if (currency == Currency.USD)
                                    accounts[accountName].IssuerOptionValue.UsdValue = issuerOptionValue;
                                else
                                    accounts[accountName].IssuerOptionValue.BaseCurrencyValue = issuerOptionValue;
                            }
                            break;
                        case "Leverage":
                            double leverage;
                            if (double.TryParse(strValue, out leverage))
                                accounts[accountName].Leverage.TotalValue = leverage;
                            accounts[accountName].Leverage.Currency = currency;
                            break;
                        case "Leverage-C":
                            double leverageC;
                            if (double.TryParse(strValue, out leverageC))
                                accounts[accountName].Leverage.StocksValue = leverageC;
                            accounts[accountName].Leverage.Currency = currency;
                            break;
                        case "Leverage-S":
                            double leverageS;
                            if (double.TryParse(strValue, out leverageS))
                                accounts[accountName].Leverage.StocksValue = leverageS;
                            accounts[accountName].Leverage.Currency = currency;
                            break;
                        case "LookAheadAvailableFunds":
                            double lookAheadLookAheadAvailableFunds;
                            if (double.TryParse(strValue, out lookAheadLookAheadAvailableFunds))
                                accounts[accountName].LookAheadAvailableFunds.TotalValue = lookAheadLookAheadAvailableFunds;
                            accounts[accountName].LookAheadAvailableFunds.Currency = currency;
                            break;
                        case "LookAheadAvailableFunds-C":
                            double lookAheadLookAheadAvailableFundsC;
                            if (double.TryParse(strValue, out lookAheadLookAheadAvailableFundsC))
                                accounts[accountName].LookAheadAvailableFunds.CommoditiesValue = lookAheadLookAheadAvailableFundsC;
                            accounts[accountName].LookAheadAvailableFunds.Currency = currency;
                            break;
                        case "LookAheadAvailableFunds-S":
                            double lookAheadLookAheadAvailableFundsS;
                            if (double.TryParse(strValue, out lookAheadLookAheadAvailableFundsS))
                                accounts[accountName].LookAheadAvailableFunds.StocksValue = lookAheadLookAheadAvailableFundsS;
                            accounts[accountName].LookAheadAvailableFunds.Currency = currency;
                            break;
                        case "LookAheadExcessLiquidity":
                            double lookAheadExcessLiquidity;
                            if (double.TryParse(strValue, out lookAheadExcessLiquidity))
                                accounts[accountName].LookAheadExcessLiquidity.TotalValue = lookAheadExcessLiquidity;
                            accounts[accountName].LookAheadExcessLiquidity.Currency = currency;
                            break;
                        case "LookAheadExcessLiquidity-C":
                            double lookAheadExcessLiquidityC;
                            if (double.TryParse(strValue, out lookAheadExcessLiquidityC))
                                accounts[accountName].LookAheadExcessLiquidity.CommoditiesValue = lookAheadExcessLiquidityC;
                            break;
                        case "LookAheadExcessLiquidity-S":
                            double lookAheadExcessLiquidityS;
                            if (double.TryParse(strValue, out lookAheadExcessLiquidityS))
                                accounts[accountName].LookAheadExcessLiquidity.StocksValue = lookAheadExcessLiquidityS;
                            accounts[accountName].LookAheadExcessLiquidity.Currency = currency;
                            break;
                        case "LookAheadInitMarginReq":
                            double lookAheadInitMarginReq;
                            if (double.TryParse(strValue, out lookAheadInitMarginReq))
                                accounts[accountName].LookAheadInitMarginReq.TotalValue = lookAheadInitMarginReq;
                            accounts[accountName].LookAheadInitMarginReq.Currency = currency;
                            break;
                        case "LookAheadInitMarginReq-C":
                            double lookAheadInitMarginReqC;
                            if (double.TryParse(strValue, out lookAheadInitMarginReqC))
                                accounts[accountName].LookAheadInitMarginReq.CommoditiesValue = lookAheadInitMarginReqC;
                            accounts[accountName].LookAheadInitMarginReq.Currency = currency;
                            break;
                        case "LookAheadInitMarginReq-S":
                            double lookAheadInitMarginReqS;
                            if (double.TryParse(strValue, out lookAheadInitMarginReqS))
                                accounts[accountName].LookAheadInitMarginReq.StocksValue = lookAheadInitMarginReqS;
                            accounts[accountName].LookAheadInitMarginReq.Currency = currency;
                            break;
                        case "LookAheadMaintMarginReq":
                            double lookAheadMaintMarginReq;
                            if (double.TryParse(strValue, out lookAheadMaintMarginReq))
                                accounts[accountName].LookAheadMaintMarginReq.TotalValue = lookAheadMaintMarginReq;
                            accounts[accountName].LookAheadMaintMarginReq.Currency = currency;
                            break;
                        case "LookAheadMaintMarginReq-C":
                            double lookAheadMaintMarginReqC;
                            if (double.TryParse(strValue, out lookAheadMaintMarginReqC))
                                accounts[accountName].LookAheadMaintMarginReq.CommoditiesValue = lookAheadMaintMarginReqC;
                            accounts[accountName].LookAheadMaintMarginReq.Currency = currency;
                            break;
                        case "LookAheadMaintMarginReq-S":
                            double lookAheadMaintMarginReqS;
                            if (double.TryParse(strValue, out lookAheadMaintMarginReqS))
                                accounts[accountName].LookAheadMaintMarginReq.StocksValue = lookAheadMaintMarginReqS;
                            accounts[accountName].LookAheadMaintMarginReq.Currency = currency;
                            break;
                        case "LookAheadNextChange":
                            double lookAheadNextChange;
                            if (double.TryParse(strValue, out lookAheadNextChange))
                                accounts[accountName].LookAheadNextChange.Value = lookAheadNextChange;
                            accounts[accountName].LookAheadNextChange.Currency = currency;
                            break;
                        case "MaintMarginReq":
                            double maintMarginReq;
                            if (double.TryParse(strValue, out maintMarginReq))
                                accounts[accountName].MaintMarginReq.TotalValue = maintMarginReq;
                            accounts[accountName].MaintMarginReq.Currency = currency;
                            break;
                        case "MaintMarginReq-C":
                            double maintMarginReqC;
                            if (double.TryParse(strValue, out maintMarginReqC))
                                accounts[accountName].MaintMarginReq.CommoditiesValue = maintMarginReqC;
                            accounts[accountName].MaintMarginReq.Currency = currency;
                            break;
                        case "MaintMarginReq-S":
                            double maintMarginReqS;
                            if (double.TryParse(strValue, out maintMarginReqS))
                                accounts[accountName].MaintMarginReq.StocksValue = maintMarginReqS;
                            accounts[accountName].MaintMarginReq.Currency = currency;
                            break;
                        case "MoneyMarketFundValue":
                            double moneyMarketFundValue;
                            if (double.TryParse(strValue, out moneyMarketFundValue))
                            {
                                if (currency == Currency.USD)
                                    accounts[accountName].MoneyMarketFundValue.UsdValue = moneyMarketFundValue;
                                else
                                    accounts[accountName].MoneyMarketFundValue.BaseCurrencyValue = moneyMarketFundValue;
                            }
                            break;
                        case "MutualFundValue":
                            double mutualFundValue;
                            if (double.TryParse(strValue, out mutualFundValue))
                            {
                                if (currency == Currency.USD)
                                    accounts[accountName].MutualFundValue.UsdValue = mutualFundValue;
                                else
                                    accounts[accountName].MutualFundValue.BaseCurrencyValue = mutualFundValue;
                            }
                            break;
                        case "NetDividend":
                            double netDividend;
                            if (double.TryParse(strValue, out netDividend))
                            {
                                if (currency == Currency.USD)
                                    accounts[accountName].NetDividend.UsdValue = netDividend;
                                else
                                    accounts[accountName].NetDividend.BaseCurrencyValue = netDividend;
                            }
                            break;
                        case "NetLiquidation":
                            double netLiquidation;
                            if (double.TryParse(strValue, out netLiquidation))
                                accounts[accountName].NetLiquidation.TotalValue = netLiquidation;
                            accounts[accountName].NetLiquidation.Currency = currency;
                            break;
                        case "NetLiquidation-C":
                            double netLiquidationC;
                            if (double.TryParse(strValue, out netLiquidationC))
                                accounts[accountName].NetLiquidation.CommoditiesValue = netLiquidationC;
                            accounts[accountName].NetLiquidation.Currency = currency;
                            break;
                        case "NetLiquidation-S":
                            double netLiquidationS;
                            if (double.TryParse(strValue, out netLiquidationS))
                                accounts[accountName].NetLiquidation.StocksValue = netLiquidationS;
                            accounts[accountName].NetLiquidation.Currency = currency;
                            break;
                        case "NetLiquidationByCurrency":
                            double netLiquidationByCurrency;
                            if (double.TryParse(strValue, out netLiquidationByCurrency))
                            {
                                if (currency == Currency.USD)
                                    accounts[accountName].NetLiquidation.TotalValue = netLiquidationByCurrency;
                                else
                                    accounts[accountName].NetLiquidation.BaseCurrencyTotalValue = netLiquidationByCurrency;
                            }
                            break;
                        case "NetLiquidationUncertainty":
                            double netLiquidationUncertainty;
                            if (double.TryParse(strValue, out netLiquidationUncertainty))
                                accounts[accountName].NetLiquidation.NetLiquidationUncertainty = netLiquidationUncertainty;
                            break;
                        case "PASharesValue":
                            double paSharesValue;
                            if (double.TryParse(strValue, out paSharesValue))
                                accounts[accountName].PASharesValue.TotalValue = paSharesValue;
                            accounts[accountName].PASharesValue.Currency = currency;
                            break;
                        case "PASharesValue-C":
                            double paSharesValueC;
                            if (double.TryParse(strValue, out paSharesValueC))
                                accounts[accountName].PASharesValue.CommoditiesValue = paSharesValueC;
                            accounts[accountName].PASharesValue.Currency = currency;
                            break;
                        case "PASharesValue-S":
                            double paSharesValueS;
                            if (double.TryParse(strValue, out paSharesValueS))
                                accounts[accountName].PASharesValue.StocksValue = paSharesValueS;
                            accounts[accountName].PASharesValue.Currency = currency;
                            break;
                        case "PostExpirationExcess":
                            double postExpirationExcess;
                            if (double.TryParse(strValue, out postExpirationExcess))
                                accounts[accountName].PostExpirationExcess.TotalValue = postExpirationExcess;
                            accounts[accountName].PostExpirationExcess.Currency = currency;
                            break;
                        case "PostExpirationExcess-C":
                            double postExpirationExcessC;
                            if (double.TryParse(strValue, out postExpirationExcessC))
                                accounts[accountName].PostExpirationExcess.CommoditiesValue = postExpirationExcessC;
                            accounts[accountName].PostExpirationExcess.Currency = currency;
                            break;
                        case "PostExpirationExcess-S":
                            double postExpirationExcessS;
                            if (double.TryParse(strValue, out postExpirationExcessS))
                                accounts[accountName].PostExpirationExcess.StocksValue = postExpirationExcessS;
                            accounts[accountName].PostExpirationExcess.Currency = currency;
                            break;
                        case "PostExpirationMargin":
                            double postExpirationMargin;
                            if (double.TryParse(strValue, out postExpirationMargin))
                                accounts[accountName].PostExpirationMargin.TotalValue = postExpirationMargin;
                            accounts[accountName].PostExpirationMargin.Currency = currency;
                            break;
                        case "PostExpirationMargin-C":
                            double postExpirationMarginC;
                            if (double.TryParse(strValue, out postExpirationMarginC))
                                accounts[accountName].PostExpirationMargin.CommoditiesValue = postExpirationMarginC;
                            accounts[accountName].PostExpirationMargin.Currency = currency;
                            break;
                        case "PostExpirationMargin-S":
                            double postExpirationMarginS;
                            if (double.TryParse(strValue, out postExpirationMarginS))
                                accounts[accountName].PostExpirationMargin.StocksValue = postExpirationMarginS;
                            accounts[accountName].PostExpirationMargin.Currency = currency;
                            break;
                        case "OptionMarketValue":
                            double optionMarketValue;
                            if (double.TryParse(strValue, out optionMarketValue))
                            {
                                if (currency == Currency.USD)
                                    accounts[accountName].OptionMarketValue.UsdValue = optionMarketValue;
                                else
                                    accounts[accountName].OptionMarketValue.BaseCurrencyValue = optionMarketValue;
                            }
                            break;
                        case "RealCurrency":
                            Currency ccy;
                            if (Enum.TryParse(strValue, out ccy))
                                accounts[accountName].BaseCurrency = ccy;
                            break;
                        case "RealizedPnL":
                            double realizedPnL;
                            if (double.TryParse(strValue, out realizedPnL))
                            {
                                if (currency == Currency.USD)
                                    accounts[accountName].RealizedPnL.UsdValue = realizedPnL;
                                else
                                    accounts[accountName].RealizedPnL.BaseCurrencyValue = realizedPnL;
                            }
                            break;
                        case "StockMarketValue":
                            double stockMarketValue;
                            if (double.TryParse(strValue, out stockMarketValue))
                            {
                                if (currency == Currency.USD)
                                    accounts[accountName].StockMarketValue.UsdValue = stockMarketValue;
                                else
                                    accounts[accountName].StockMarketValue.BaseCurrencyValue = stockMarketValue;
                            }
                            break;
                        case "TotalCashValue":
                            double totalCashValue;
                            if (double.TryParse(strValue, out totalCashValue))
                                accounts[accountName].TotalCashValue.TotalValue = totalCashValue;
                            accounts[accountName].TotalCashValue.Currency = currency;
                            break;
                        case "TBillValue":
                            double tBillValue;
                            if (double.TryParse(strValue, out tBillValue))
                            {
                                if (currency == Currency.USD)
                                    accounts[accountName].TBillValue.UsdValue = tBillValue;
                                else
                                    accounts[accountName].TBillValue.BaseCurrencyValue = tBillValue;
                            }
                            break;
                        case "TBondValue":
                            double tBondValue;
                            if (double.TryParse(strValue, out tBondValue))
                            {
                                if (currency == Currency.USD)
                                    accounts[accountName].TBondValue.UsdValue = tBondValue;
                                else
                                    accounts[accountName].TBondValue.BaseCurrencyValue = tBondValue;
                            }
                            break;
                        case "TotalCashValue-C":
                            double totalCashValueC;
                            if (double.TryParse(strValue, out totalCashValueC))
                                accounts[accountName].TotalCashValue.CommoditiesValue = totalCashValueC;
                            accounts[accountName].TotalCashValue.Currency = currency;
                            break;
                        case "TotalCashValue-S":
                            double totalCashValueS;
                            if (double.TryParse(strValue, out totalCashValueS))
                                accounts[accountName].TotalCashValue.StocksValue = totalCashValueS;
                            accounts[accountName].TotalCashValue.Currency = currency;
                            break;
                        case "TotalCashBalance":
                            double totalCashBalance;
                            if (double.TryParse(strValue, out totalCashBalance))
                            {
                                if (currency == Currency.USD)
                                    accounts[accountName].TotalCashBalance.UsdValue = totalCashBalance;
                                else
                                    accounts[accountName].TotalCashBalance.BaseCurrencyValue = totalCashBalance;
                            }
                            break;
                        case "UnrealizedPnL":
                            double unrealizedPnL;
                            if (double.TryParse(strValue, out unrealizedPnL))
                            {
                                if (currency == Currency.USD)
                                    accounts[accountName].UnrealizedPnL.UsdValue = unrealizedPnL;
                                else
                                    accounts[accountName].UnrealizedPnL.BaseCurrencyValue = unrealizedPnL;
                            }
                            break;
                        case "WarrantValue":
                            double warrantValue;
                            if (double.TryParse(strValue, out warrantValue))
                            {
                                if (currency == Currency.USD)
                                    accounts[accountName].WarrantValue.UsdValue = warrantValue;
                                else
                                    accounts[accountName].WarrantValue.BaseCurrencyValue = warrantValue;
                            }
                            break;
                        default:
                            logger.Debug($"Ignoring account attribute {key}={strValue}");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    logger.Error("Caught exception in AccountDetailsAttributeReceived", ex);
                }
            }
        }

        private void AccountUpdateTimeReceived(DateTimeOffset lastUpdate)
        {
            if (!string.IsNullOrEmpty(activeAccount))
                accounts[activeAccount].LastUpdate = lastUpdate;
        }

        private void AccountDetailsEnded(string account)
        {
            accountDetailsReceivedEvent?.Set();
        }
        #endregion

        #region Account Summary
        private AutoResetEvent accountSummaryReceivedEvent;
        private bool isRequestingAccountSummary;
        private object isRequestingAccountSummaryLocker = new object();
        private int accountSummaryRequestId;
        private Account summarizedAccount = null;

        public async Task<(bool Success, string Message)> RequestAccountsSummary()
        {
            if (isRequestingAccountSummary)
                return (false, "Unable to request account summary: another request is already in progress. Try again later");

            SetIsRequestingAccountSummaryFlag(true);

            return await Task.Run(() =>
            {
                try
                {
                    stopRequestedCt.ThrowIfCancellationRequested();

                    accountSummaryReceivedEvent = new AutoResetEvent(false);

                    accountSummaryRequestId = (new Random()).Next(10000, 11000);

                    ibClient.ResponseManager.AccountSummaryReceived += AccountSummaryAttributeReceived;
                    ibClient.ResponseManager.AccountSummaryEnded += AccountSummaryEnded;

                    ibClient.RequestManager.AccountRequestManager.RequestAccountSummary(accountSummaryRequestId, new string[1] { "$LEDGER:USD" }, "Group1");

                    bool received = accountSummaryReceivedEvent.WaitOne(TimeSpan.FromSeconds(3));

                    ibClient.RequestManager.AccountRequestManager.CancelAccountSummaryRequest(accountSummaryRequestId);

                    ibClient.ResponseManager.AccountSummaryReceived -= AccountSummaryAttributeReceived;
                    ibClient.ResponseManager.AccountSummaryEnded -= AccountSummaryEnded;

                    if (received)
                        return (true, "Received account summary");
                    else
                        return (false, "Didn't receive any account summary in the specified timeframe of 3 seconds");
                }
                catch (OperationCanceledException)
                {
                    string err = "Not requesting account summary: operation cancelled";
                    logger.Error(err);
                    return (false, err);
                }
                catch (Exception ex)
                {
                    string err = "Failed to request account summary";
                    logger.Error(err, ex);
                    return (false, $"{err}: {ex.Message}");
                }
                finally
                {
                    SetIsRequestingAccountSummaryFlag(false);
                }
            }, stopRequestedCt);
        }

        private void SetIsRequestingAccountSummaryFlag(bool value)
        {
            lock (isRequestingAccountSummaryLocker)
            {
                summarizedAccount = null;
                isRequestingAccountSummary = value;
            }
        }

        private void AccountSummaryAttributeReceived(int reqId, string account, string modelCode, string key, string value, string currency)
        {
            logger.Info("Acct Summary. ReqId: " + reqId + ", Acct: " + account + ", Key: " + key + ", Value: " + value + ", Currency: " + currency);
        }

        private void AccountSummaryEnded(int obj)
        {
            accountSummaryReceivedEvent?.Set();
        }
        #endregion
    }
}
