﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using log4net;
using System.Collections.Concurrent;
using System.Threading;
using Capital.GSG.FX.Trading.Executor.Core;
using Capital.GSG.FX.Data.Core.ContractData;
using Capital.GSG.FX.Data.Core.MarketData;
using static Capital.GSG.FX.Data.Core.MarketData.MarketDataTickType;
using Capital.GSG.FX.Utils.Core;
using static Capital.GSG.FX.Data.Core.SystemData.SystemStatusLevel;
using IBData;
using System.Text;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI.Executor
{
    public class IBMarketDataProvider : IMarketDataProvider
    {
        private static ILog logger = LogManager.GetLogger(nameof(IBMarketDataProvider));

        private readonly Dictionary<int, FXMarketDataRequest> fxMarketDataRequests = new Dictionary<int, FXMarketDataRequest>();

        private readonly Dictionary<int, FutureMarketDataRequest> futureMarketDataRequests = new Dictionary<int, FutureMarketDataRequest>();
        private readonly Dictionary<(string Exchange, string Symbol, double Multiplier, MarketDataRequestType Type), int> futureMarketDataRequestsBySymbol = new Dictionary<(string Exchange, string Symbol, double Multiplier, MarketDataRequestType Type), int>();

        private readonly ConcurrentDictionary<Cross, RTBar> currentRtBars = new ConcurrentDictionary<Cross, RTBar>();
        private readonly ConcurrentDictionary<(string Symbol, DateTime Expiry), FutMarketDataTick> currentFutureTicks = new ConcurrentDictionary<(string Symbol, DateTime Expiry), FutMarketDataTick>();

        private readonly MarketDataTickType[] interestingSizeTickTypes = { BID_SIZE, ASK_SIZE };
        private readonly MarketDataTickType[] interestingPriceTickTypes = { BID, ASK };

        private readonly BrokerClient brokerClient;
        private readonly IBClient ibClient;
        private readonly bool logTicks;
        private readonly CancellationToken stopRequestedCt;

        private ConcurrentDictionary<Cross, bool> currentMdTicksSubscribed = new ConcurrentDictionary<Cross, bool>();
        private ConcurrentDictionary<Cross, bool> currentRtBarsSubscribed = new ConcurrentDictionary<Cross, bool>();
        private ConcurrentDictionary<(string Exchange, string Symbol, double Multiplier, DateTime Expiry), bool> currentFuturesSubscribed = new ConcurrentDictionary<(string Exchange, string Symbol, double Multiplier, DateTime Expiry), bool>();

        private DateTimeOffset lastMarketDataLostCheck = DateTimeOffset.MinValue;
        private object lastMarketDataLostCheckLocker = new object();
        private DateTimeOffset lastHistoricalDataLostCheck = DateTimeOffset.MinValue;
        private object lastHistoricalDataLostCheckLocker = new object();
        private DateTimeOffset lastMarketDataResumedCheck = DateTimeOffset.MinValue;
        private object lastMarketDataResumedCheckLocker = new object();
        private DateTimeOffset lastHistoricalDataResumedCheck = DateTimeOffset.MinValue;
        private object lastHistoricalDataResumedCheckLocker = new object();

        public event Action MarketDataConnectionLost;
        public event Action HistoricalDataConnectionLost;
        public event Action MarketDataConnectionResumed;
        public event Action HistoricalDataConnectionResumed;

        internal IBMarketDataProvider(BrokerClient brokerClient, IBClient ibClient, IEnumerable<Contract> ibContracts, IEnumerable<FutureContract> ibFutContracts, bool logTicks, CancellationToken stopRequestedCt)
        {
            this.brokerClient = brokerClient ?? throw new ArgumentNullException(nameof(brokerClient));

            this.stopRequestedCt = stopRequestedCt;

            this.ibClient = ibClient ?? throw new ArgumentNullException(nameof(ibClient));

            this.ibClient.IBConnectionLost += () =>
            {
                logger.Error("IB client disconnected from TWS. Send out a notification that market and historical data are disconnected");

                HandleHistoricalDataDisconnection();
                HandleMarketDataDisconnection();

                logger.Error("IB client disconnected from TWS. Clearing current RT bars cache, if any");

                currentRtBars.Clear();
            };

            this.ibClient.IBConnectionEstablished += () =>
            {
                logger.Info("IB client (re)connected. Will resubmit MD ticks and RT bars requests, if any");

                ResubmitPreviousMarketDataRequests();
            };

            this.logTicks = logTicks;

            SetupMarketDataRequests(ibContracts, ibFutContracts);

            SetupEventListeners();
        }

        private void SetupMarketDataRequests(IEnumerable<Contract> ibContracts, IEnumerable<FutureContract> ibFutureContracts)
        {
            if (!ibContracts.IsNullOrEmpty())
            {
                fxMarketDataRequests.Clear();

                int counter = 10;
                foreach (Contract contract in ibContracts)
                {
                    fxMarketDataRequests.Add(counter + 0, new FXMarketDataRequest(counter + 0, contract, MarketDataRequestType.MarketDataTick));
                    fxMarketDataRequests.Add(counter + 1, new FXMarketDataRequest(counter + 1, contract, MarketDataRequestType.RtBarBid));
                    fxMarketDataRequests.Add(counter + 2, new FXMarketDataRequest(counter + 2, contract, MarketDataRequestType.RtBarMid));
                    fxMarketDataRequests.Add(counter + 3, new FXMarketDataRequest(counter + 3, contract, MarketDataRequestType.RtBarAsk));

                    counter += 10;
                }
            }

            if (!ibFutureContracts.IsNullOrEmpty())
            {
                futureMarketDataRequests.Clear();
                futureMarketDataRequestsBySymbol.Clear();

                int counter = 2000;
                foreach (var contract in ibFutureContracts)
                {
                    futureMarketDataRequests.Add(counter + 0, new FutureMarketDataRequest(counter + 0, contract, MarketDataRequestType.MarketDataTick));
                    futureMarketDataRequestsBySymbol.Add((contract.Exchange, contract.Symbol, contract.Multiplier, MarketDataRequestType.MarketDataTick), counter + 0);

                    // TODO : RT Bars requests

                    counter += 10;
                }
            }
        }

        private void SetupEventListeners()
        {
            ibClient.ResponseManager.MarketDataSizeTickReceived += MarketDataSizeTickReceived;
            ibClient.ResponseManager.MarketDataPriceTickReceived += MarketDataPriceTickReceived;
            ibClient.ResponseManager.RealTimeBarReceived += RealTimeBarReceived;
        }

        private void MarketDataSizeTickReceived(int requestID, MarketDataTickType tickType, int size)
        {
            if (size < 0)
            {
                logger.Warn($"Ignoring invalid size tick for request {requestID}: {nameof(tickType)}={tickType} {nameof(size)}={size}");
                return;
            }

            // 1. Try parse FX request
            var fxRequest = fxMarketDataRequests.GetValueOrDefault(requestID);

            if (fxRequest != null && fxRequest.Type == MarketDataRequestType.MarketDataTick)
            {
                Cross cross = fxRequest.Contract.Cross;

                if (size > 0 && interestingSizeTickTypes.Contains(tickType))
                {
                    if (logTicks)
                        logger.Debug($"Received size tick data: requestID={requestID}, cross={cross}, tickType={tickType}, size={size}");

                    brokerClient.TradingExecutorRunner?.OnMdSizeTick(new SizeTick(cross, tickType, size));
                }
                else
                    logger.Debug($"Received size tick data with 0 size. Not recording: requestID={requestID}, cross={cross}, tickType={tickType}, size={size}");

                return;
            }

            // 2. Otherwise try parse Fut request
            var futRequest = futureMarketDataRequests.GetValueOrDefault(requestID);

            if (futRequest != null && futRequest.Type == MarketDataRequestType.MarketDataTick)
            {
                FutureContract contract = futRequest.Contract;

                // TODO
                var tick = currentFutureTicks.AddOrUpdate((contract.Symbol, contract.CurrentExpi), key =>
                {
                    var newTick = new FutMarketDataTick() { Symbol = contract.Symbol, Expiry = contract.CurrentExpi };
                    EnrichFutPriceTick(tickType, size, ref newTick);
                    return newTick;
                }, (key, oldValue) =>
                {
                    EnrichFutPriceTick(tickType, size, ref oldValue);
                    return oldValue;
                });

                logger.Debug($"Fut tick update: {tick.Timestamp}|{tick.Symbol}|{tick.Expiry:yyyyMMdd}|Ask={tick.Ask}|AskSize={tick.AskSize}|Bid={tick.Bid}|BidSize={tick.BidSize}|Volume={tick.DayVolume}|Open={tick.DayOpen}|High={tick.DayHigh}|Low={tick.DayLow}|Close={tick.PrevDayClose}|LastTradePrice={tick.LastTradePrice}|LastTradeSize={tick.LastTradeSize}");

                brokerClient.TradingExecutorRunner?.OnFutureTick(tick);
            }
        }

        private void MarketDataPriceTickReceived(int requestID, MarketDataTickType tickType, double value, bool canAutoExecute)
        {
            if (value <= 0)
            {
                logger.Warn($"Ignoring invalid price tick for request {requestID}: {nameof(tickType)}={tickType} {nameof(value)}={value}");
                return;
            }

            // 1. Try parse FX request
            var fxRequest = fxMarketDataRequests.GetValueOrDefault(requestID);

            if (fxRequest != null && fxRequest.Type == MarketDataRequestType.MarketDataTick)
            {
                Cross cross = fxRequest.Contract.Cross;

                if (interestingPriceTickTypes.Contains(tickType))
                {
                    if (logTicks)
                        logger.Debug($"Received price tick data: requestID={requestID}, cross={cross}, type={tickType}, value={value}, canAutoExecute={canAutoExecute}");

                    brokerClient.TradingExecutorRunner?.OnMdPriceTick(new PriceTick(cross, tickType, value, canAutoExecute));
                }
                else
                    logger.Debug($"Received price tick data with 0.0 value. Not recording: requestID={requestID}, cross={cross}, type={tickType}, value={value}, canAutoExecute={canAutoExecute}");

                return;
            }

            // 2. Otherwise try parse CME Fut request
            var cmeFutRequest = futureMarketDataRequests.GetValueOrDefault(requestID);

            if (cmeFutRequest != null && cmeFutRequest.Type == MarketDataRequestType.MarketDataTick)
            {
                FutureContract contract = cmeFutRequest.Contract;

                var tick = currentFutureTicks.AddOrUpdate((contract.Symbol, contract.CurrentExpi), key =>
                {
                    var newTick = new FutMarketDataTick() { Symbol = contract.Symbol, Expiry = contract.CurrentExpi };
                    EnrichFutPriceTick(tickType, value, ref newTick);
                    return newTick;
                }, (key, oldValue) =>
                {
                    EnrichFutPriceTick(tickType, value, ref oldValue);
                    return oldValue;
                });

                logger.Debug($"CME Fut tick update: {tick.Timestamp}|{tick.Symbol}|{tick.Expiry:yyyyMMdd}|Ask={tick.Ask}|AskSize={tick.AskSize}|Bid={tick.Bid}|BidSize={tick.BidSize}|Volume={tick.DayVolume}|Open={tick.DayOpen}|High={tick.DayHigh}|Low={tick.DayLow}|Close={tick.PrevDayClose}|LastTradePrice={tick.LastTradePrice}|LastTradeSize={tick.LastTradeSize}");

                brokerClient.TradingExecutorRunner?.OnFutureTick(tick);
            }
        }

        private void EnrichFutPriceTick(MarketDataTickType tickType, double value, ref FutMarketDataTick tick)
        {
            tick.Timestamp = DateTimeOffset.Now;

            switch (tickType)
            {
                case BID:
                    tick.Bid = value;
                    break;
                case BID_SIZE:
                    tick.BidSize = value;
                    break;
                case ASK:
                    tick.Ask = value;
                    break;
                case ASK_SIZE:
                    tick.AskSize = value;
                    break;
                case HIGH:
                    tick.DayHigh = value;
                    break;
                case LOW:
                    tick.DayLow = value;
                    break;
                case CLOSE:
                    tick.PrevDayClose = value;
                    break;
                case OPEN:
                    tick.DayOpen = value;
                    break;
                case LAST:
                    tick.LastTradePrice = value;
                    break;
                case LAST_SIZE:
                    tick.LastTradeSize = value;
                    break;
                case VOLUME:
                    tick.DayVolume = value;
                    break;
                default:
                    break;
            }
        }

        private void RealTimeBarReceived(int requestID, DateTime time, double open, double high, double low, double close, TimeSpan delay)
        {
            // 1. Try parse FX request
            var fxRequest = fxMarketDataRequests.GetValueOrDefault(requestID);

            if (fxRequest != null && fxRequest.Type != MarketDataRequestType.MarketDataTick)
            {
                Cross cross = fxRequest.Contract.Cross;

                double delayInMs = Math.Round(delay.TotalMilliseconds, 0);

                if (logTicks)
                    logger.Debug($"Received real time bar: requestID={requestID}, cross={cross}, type={fxMarketDataRequests[requestID].Type}, time={time}, open={open}, high={high}, low={low}, close={close}, delay={delayInMs:N0}ms");

                brokerClient.UpdateStatus("RTBarDelayInMs", delayInMs, delayInMs < 15000 ? GREEN : delayInMs < 30000 ? YELLOW : RED);

                RTBarPoint rtBarPoint = new RTBarPoint(time, cross, open, high, low, close);

                RTBar currentRtBar;
                if (currentRtBars.TryGetValue(cross, out currentRtBar) && currentRtBar != null)
                {
                    if (currentRtBar == null)
                    {
                        logger.Error($"currentRtBars dictionary was holding a null value for {cross}. This is unexpected");
                        return;
                    }
                    else if (currentRtBar.Timestamp < time)
                    {
                        logger.Error($"currentRtBars dictionary was holding a value for {cross} with an unexpected timestamp (expected: {time}, actual: {currentRtBar.Timestamp}). Will fast-forward to the timestamp of the new RT bar point");

                        RTBar fastForwardRtBar = new RTBar(time, cross);

                        switch (fxMarketDataRequests[requestID].Type)
                        {
                            case MarketDataRequestType.RtBarAsk:
                                fastForwardRtBar.Ask = rtBarPoint;
                                break;
                            case MarketDataRequestType.RtBarBid:
                                fastForwardRtBar.Bid = rtBarPoint;
                                break;
                            case MarketDataRequestType.RtBarMid:
                                fastForwardRtBar.Mid = rtBarPoint;
                                break;
                            default:
                                break;
                        }

                        if (!currentRtBars.TryUpdate(cross, fastForwardRtBar, currentRtBar))
                            logger.Error($"Failed to update RT bar {fastForwardRtBar} in dictionary");

                        return;
                    }
                    else if (currentRtBar.Timestamp > time)
                    {
                        logger.Error($"currentRtBars dictionary was holding a value for {cross} with an unexpected timestamp (expected: {time}, actual: {currentRtBar.Timestamp}). Ignoring the new RT bar point as it is older");

                        return;
                    }
                    else
                    {
                        RTBar updatedRtBar = new RTBar(time, cross)
                        {
                            Ask = currentRtBar.Ask,
                            Bid = currentRtBar.Bid,
                            Mid = currentRtBar.Mid
                        };

                        switch (fxMarketDataRequests[requestID].Type)
                        {
                            case MarketDataRequestType.RtBarAsk:
                                updatedRtBar.Ask = rtBarPoint;
                                break;
                            case MarketDataRequestType.RtBarBid:
                                updatedRtBar.Bid = rtBarPoint;
                                break;
                            case MarketDataRequestType.RtBarMid:
                                updatedRtBar.Mid = rtBarPoint;
                                break;
                            default:
                                break;
                        }

                        if (updatedRtBar.Ask != null && updatedRtBar.Bid != null && updatedRtBar.Mid != null)
                        {
                            if (logTicks)
                                logger.Debug($"Posting RT bar {updatedRtBar}");

                            brokerClient.TradingExecutorRunner?.OnMdRtBar(updatedRtBar);

                            RTBar discarded;
                            if (!currentRtBars.TryRemove(cross, out discarded))
                                logger.Error($"Failed to remove currentRtBar from dictionary: {currentRtBar}");
                        }
                        else
                        {
                            if (logTicks)
                                logger.Debug($"Not posting incomplete RT bar {updatedRtBar}");

                            if (!currentRtBars.TryUpdate(cross, updatedRtBar, currentRtBar))
                                logger.Error($"Failed to update RT bar {updatedRtBar} in dictionary");
                        }
                    }
                }
                else
                {
                    RTBar newRtBar = new RTBar(time, cross);

                    switch (fxMarketDataRequests[requestID].Type)
                    {
                        case MarketDataRequestType.RtBarAsk:
                            newRtBar.Ask = rtBarPoint;
                            break;
                        case MarketDataRequestType.RtBarBid:
                            newRtBar.Bid = rtBarPoint;
                            break;
                        case MarketDataRequestType.RtBarMid:
                            newRtBar.Mid = rtBarPoint;
                            break;
                        default:
                            break;
                    }

                    if (logTicks)
                        logger.Debug($"Adding new incomplete RT bar to the dictionary: {newRtBar}");

                    if (!currentRtBars.TryAdd(cross, newRtBar))
                        logger.Error($"Failed to add new incomplete RT bar to the dictionary: {newRtBar}");
                }

                return;
            }

            // 2. Otherwise try parse CME Fut request
            var cmeFutRequest = futureMarketDataRequests.GetValueOrDefault(requestID);

            if (cmeFutRequest != null && cmeFutRequest.Type == MarketDataRequestType.MarketDataTick)
            {
                FutureContract contract = cmeFutRequest.Contract;

                // TODO
            }
        }

        private int GetFXRequestIdForCrossAndRequestType(Cross cross, MarketDataRequestType type, bool submitted)
        {
            return fxMarketDataRequests.Where(r => r.Value.Contract.Cross == cross && r.Value.Type == type && r.Value.Submitted == submitted).Select(r => r.Key).FirstOrDefault();
        }

        internal string GetMarketDataRequestDetails(int requestId)
        {
            // 1. Try parse FX request
            var fxRequest = fxMarketDataRequests.GetValueOrDefault(requestId);

            if (fxRequest != null)
                return $"{fxRequest.Contract.Cross} - {fxRequest.Type}";

            // 2. Otherwise try parse CME Fut request
            var cmeFutRequest = futureMarketDataRequests.GetValueOrDefault(requestId);

            if (cmeFutRequest != null)
                return $"{cmeFutRequest.Contract.Symbol} ({cmeFutRequest.Contract.Description} - {cmeFutRequest.Contract.CurrentExpi:yyyyMMdd}) - {fxRequest.Type}";

            return "unknown";
        }

        private IEnumerable<int> GetFXRequestIdsForCrossAndRequestTypes(Cross cross, IEnumerable<MarketDataRequestType> types, bool submitted)
        {
            if (types.IsNullOrEmpty())
                return null;
            else
                return fxMarketDataRequests.Where(r => r.Value.Contract.Cross == cross && types.Contains(r.Value.Type) && r.Value.Submitted == submitted).Select(r => r.Key);
        }

        private IEnumerable<int> GetRequestIdsForRequestTypes(IEnumerable<MarketDataRequestType> types, bool submitted)
        {
            if (types.IsNullOrEmpty())
                return null;
            else
                return fxMarketDataRequests.Where(r => types.Contains(r.Value.Type) && r.Value.Submitted == submitted).Select(r => r.Key);
        }

        private IEnumerable<int> GetFXRequestIdsForCrossesAndRequestType(IEnumerable<Cross> crosses, MarketDataRequestType type, bool submitted)
        {
            if (crosses.IsNullOrEmpty())
                return null;
            else
                return fxMarketDataRequests.Where(r => crosses.Contains(r.Value.Contract.Cross) && r.Value.Type == type && r.Value.Submitted == submitted).Select(r => r.Key);
        }

        private IEnumerable<int> GetRequestIdsForRequestType(MarketDataRequestType type, bool submitted)
        {
            return GetRequestIdsForRequestTypes(new MarketDataRequestType[1] { type }, submitted);
        }

        private IEnumerable<int> GetFXRequestIdsForCrossesAndRequestTypes(IEnumerable<Cross> crosses, IEnumerable<MarketDataRequestType> types, bool submitted)
        {
            if (crosses.IsNullOrEmpty() || types.IsNullOrEmpty())
                return null;
            else
                return fxMarketDataRequests.Where(r => crosses.Contains(r.Value.Contract.Cross) && types.Contains(r.Value.Type) && r.Value.Submitted == submitted).Select(r => r.Key);
        }

        private void ResubmitPreviousMarketDataRequests()
        {
            int[] requestIds = fxMarketDataRequests.Where(r => r.Value.Submitted).Select(r => r.Key).Distinct().ToArray();

            if (!requestIds.IsNullOrEmpty())
            {
                logger.Info($"Resubmitting requests {string.Join(", ", requestIds)}");

                CancelMarketDataRequests(requestIds);

                Task.Delay(TimeSpan.FromSeconds(2)).Wait();

                SubmitMarketDataRequests(requestIds);
            }
            else
                logger.Info("No request to resubmit");
        }

        private void SubmitMarketDataRequest(int requestId)
        {
            // 1. Try parse FX request
            var fxRequest = fxMarketDataRequests.GetValueOrDefault(requestId);

            if (fxRequest != null)
            {
                logger.Info($"Submitting FX market data request {requestId} ({fxRequest.Contract.Cross} - {fxRequest.Type})");

                switch (fxRequest.Type)
                {
                    case MarketDataRequestType.MarketDataTick:
                        ibClient.RequestManager.MarketDataRequestManager.RequestMarketData(requestId, fxRequest.Contract);
                        break;
                    case MarketDataRequestType.RtBarBid:
                        ibClient.RequestManager.RealTimeBarsRequestManager.RequestRealTimeBars(requestId, fxRequest.Contract, "BID", true);
                        break;
                    case MarketDataRequestType.RtBarMid:
                        ibClient.RequestManager.RealTimeBarsRequestManager.RequestRealTimeBars(requestId, fxRequest.Contract, "MIDPOINT", true);
                        break;
                    case MarketDataRequestType.RtBarAsk:
                        ibClient.RequestManager.RealTimeBarsRequestManager.RequestRealTimeBars(requestId, fxRequest.Contract, "ASK", true);
                        break;
                    default:
                        break;
                }

                fxRequest.Submitted = true;

                Task.Delay(TimeSpan.FromSeconds(1)).Wait();

                return;
            }

            // 2. Otherwise try parse CME Fut request
            var futRequest = futureMarketDataRequests.GetValueOrDefault(requestId);

            if (futRequest != null)
            {
                logger.Info($"Submitting CME Future market data request {requestId} ({futRequest.Contract.Symbol} - {fxRequest.Type})");

                switch (futRequest.Type)
                {
                    case MarketDataRequestType.MarketDataTick:
                        ibClient.RequestManager.MarketDataRequestManager.RequestFuture(requestId, futRequest.Contract.Exchange, futRequest.Contract.Symbol, futRequest.Contract.Multiplier, futRequest.Contract.Currency, futRequest.Contract.CurrentExpi);
                        break;
                    //case MarketDataRequestType.RtBarBid:
                    //    ibClient.RequestManager.RealTimeBarsRequestManager.RequestRealTimeBars(requestId, fxRequest.Contract, "BID", true);
                    //    break;
                    //case MarketDataRequestType.RtBarMid:
                    //    ibClient.RequestManager.RealTimeBarsRequestManager.RequestRealTimeBars(requestId, fxRequest.Contract, "MIDPOINT", true);
                    //    break;
                    //case MarketDataRequestType.RtBarAsk:
                    //    ibClient.RequestManager.RealTimeBarsRequestManager.RequestRealTimeBars(requestId, fxRequest.Contract, "ASK", true);
                    //    break;
                    default:
                        logger.Error($"CME Future market data request of type {futRequest.Type} is not yet supported");
                        break;
                }

                futRequest.Submitted = true;

                Task.Delay(TimeSpan.FromSeconds(1)).Wait();

                return;
            }

            // Request is unknown
            logger.Error($"Unable to submit request for unknown requestId {requestId}");
        }

        private void SubmitMarketDataRequests(IEnumerable<int> requestIds)
        {
            if (!requestIds.IsNullOrEmpty())
            {
                foreach (int requestId in requestIds)
                    SubmitMarketDataRequest(requestId);
            }
        }

        private void CancelMarketDataRequest(int requestId)
        {
            // 1. Try parse FX request
            var fxRequest = fxMarketDataRequests.GetValueOrDefault(requestId);

            if (fxRequest != null)
            {
                logger.Info($"Requesting cancellation of FX market data request {requestId} ({fxRequest.Contract.Cross} - {fxRequest.Type})");

                switch (fxRequest.Type)
                {
                    case MarketDataRequestType.MarketDataTick:
                        ibClient.RequestManager.MarketDataRequestManager.CancelMarketDataRequest(requestId);
                        break;
                    case MarketDataRequestType.RtBarBid:
                    case MarketDataRequestType.RtBarMid:
                    case MarketDataRequestType.RtBarAsk:
                        ibClient.RequestManager.RealTimeBarsRequestManager.CancelRealTimeBarsRequest(requestId);
                        break;
                    default:
                        break;
                }

                fxRequest.Submitted = false;

                return;
            }

            // 2. Otherwise try parse CME Fut request
            var cmeFutRequest = futureMarketDataRequests.GetValueOrDefault(requestId);

            if (cmeFutRequest != null)
            {
                logger.Info($"Requesting cancellation of CME Future market data request {requestId} ({cmeFutRequest.Contract.Symbol} - {cmeFutRequest.Type})");

                switch (cmeFutRequest.Type)
                {
                    case MarketDataRequestType.MarketDataTick:
                        ibClient.RequestManager.MarketDataRequestManager.CancelMarketDataRequest(requestId);
                        break;
                    case MarketDataRequestType.RtBarBid:
                    case MarketDataRequestType.RtBarMid:
                    case MarketDataRequestType.RtBarAsk:
                        ibClient.RequestManager.RealTimeBarsRequestManager.CancelRealTimeBarsRequest(requestId);
                        break;
                    default:
                        break;
                }

                cmeFutRequest.Submitted = false;

                return;
            }

            // Request is unknown
            logger.Error($"Unable to cancel request for unknown requestId {requestId}");
        }

        private void CancelMarketDataRequests(IEnumerable<int> requestIds)
        {
            if (!requestIds.IsNullOrEmpty())
                foreach (int requestId in requestIds)
                    CancelMarketDataRequest(requestId);
        }

        public void ReplaceMDTicksSubscriptions(IEnumerable<Cross> crosses)
        {
            ReplaceFXMarketDataRequests(crosses, new MarketDataRequestType[1] { MarketDataRequestType.MarketDataTick });
        }

        public void ReplaceRTBarsSubscriptions(IEnumerable<Cross> crosses)
        {
            ReplaceFXMarketDataRequests(crosses, new MarketDataRequestType[3] { MarketDataRequestType.RtBarAsk, MarketDataRequestType.RtBarBid, MarketDataRequestType.RtBarMid });
        }

        private void ReplaceFXMarketDataRequests(IEnumerable<Cross> crosses, IEnumerable<MarketDataRequestType> types)
        {
            if (crosses.IsNullOrEmpty())
                CancelMarketDataRequests(GetRequestIdsForRequestTypes(types, true));
            else
            {
                // 1. Cancel requests that were removed
                CancelMarketDataRequests(fxMarketDataRequests.Where(r => !crosses.Contains(r.Value.Contract.Cross) && types.Contains(r.Value.Type) && r.Value.Submitted)?.Select(r => r.Key));

                // 2. Subscribe newly added requests
                SubmitMarketDataRequests(fxMarketDataRequests.Where(r => crosses.Contains(r.Value.Contract.Cross) && types.Contains(r.Value.Type) && !r.Value.Submitted)?.Select(r => r.Key));
            }
        }

        public void SubscribeMDTicks(IEnumerable<Cross> crosses)
        {
            if (!crosses.IsNullOrEmpty())
            {
                logger.Info($"Subscribing MD ticks for {string.Join(", ", crosses)}");

                SubmitMarketDataRequests(GetFXRequestIdsForCrossesAndRequestType(crosses, MarketDataRequestType.MarketDataTick, false));

                foreach (var cross in crosses)
                    currentMdTicksSubscribed.AddOrUpdate(cross, true, (key, oldValue) => true);
            }
        }

        public void SubscribeMDTicks(Cross cross)
        {
            logger.Info($"Subscribing MD ticks for {cross}");

            SubmitMarketDataRequest(GetFXRequestIdForCrossAndRequestType(cross, MarketDataRequestType.MarketDataTick, false));

            currentMdTicksSubscribed.AddOrUpdate(cross, true, (key, oldValue) => true);
        }

        public (bool Success, string Message) SubscribeFutures(IEnumerable<(string Exchange, string Symbol, double Multiplier)> futures)
        {
            try
            {
                stopRequestedCt.ThrowIfCancellationRequested();

                if (futures.IsNullOrEmpty())
                    throw new ArgumentNullException(nameof(futures));

                foreach (var future in futures)
                {
                    // 1. Market data ticks
                    int requestId = futureMarketDataRequestsBySymbol.GetValueOrDefault((future.Exchange, future.Symbol, future.Multiplier, MarketDataRequestType.MarketDataTick));
                    var request = futureMarketDataRequests.GetValueOrDefault(requestId);

                    if (request != null)
                    {
                        ibClient.RequestManager.MarketDataRequestManager.RequestFuture(requestId, request.Contract.Exchange, request.Contract.Symbol, request.Contract.Multiplier, request.Contract.Currency, request.Contract.CurrentExpi);
                        request.Submitted = true;
                        currentFuturesSubscribed.AddOrUpdate((future.Exchange, future.Symbol, future.Multiplier, request.Contract.CurrentExpi), true, (key, oldValue) => true);
                    }
                    else
                        logger.Error($"Failed to locate Future market data request for {future}");

                    // 2. TODO: RT bars
                }

                return (true, "");
            }
            catch (OperationCanceledException)
            {
                string err = "Not subscribing Future market data: operation cancelled";
                logger.Error(err);
                return (false, err);
            }
            catch (ArgumentNullException ex)
            {
                string err = $"Not subscribing Future market data: missing or invalid parameter {ex.ParamName}";
                logger.Error(err);
                return (false, err);
            }
            catch (Exception ex)
            {
                string err = "Failed to subscribe to Futures market data";
                logger.Error(err, ex);
                return (false, $"{err}: {ex.Message}");
            }
        }

        public (bool Success, string Message) SubscribeFutures(string exchange, string symbol, double multiplier)
        {
            return SubscribeFutures(new(string, string, double)[1] { (exchange, symbol, multiplier) });
        }

        public (bool Success, string Message) UnsubscribeFutures(string exchange, string symbol, double multiplier)
        {
            return UnsubscribeFutures(new(string, string, double)[1] { (exchange, symbol, multiplier) });
        }

        public (bool Success, string Message) UnsubscribeFutures(IEnumerable<(string Exchange, string Symbol, double Multiplier)> futures)
        {
            try
            {
                stopRequestedCt.ThrowIfCancellationRequested();

                if (futures.IsNullOrEmpty())
                    throw new ArgumentNullException(nameof(futures));

                foreach (var future in futures)
                {
                    // 1. Market data ticks
                    int requestId = futureMarketDataRequestsBySymbol.GetValueOrDefault((future.Exchange, future.Symbol, future.Multiplier, MarketDataRequestType.MarketDataTick));
                    var request = futureMarketDataRequests.GetValueOrDefault(requestId);

                    if (request != null)
                    {
                        ibClient.RequestManager.MarketDataRequestManager.CancelMarketDataRequest(requestId);
                        request.Submitted = false;
                        currentFuturesSubscribed.AddOrUpdate((future.Exchange, future.Symbol, future.Multiplier, request.Contract.CurrentExpi), false, (key, oldValue) => false);
                    }
                    else
                        logger.Error($"Failed to locate Future market data request for {future}");

                    // 2. TODO: RT bars
                }

                return (true, "");
            }
            catch (OperationCanceledException)
            {
                string err = "Not unsubscribing Future market data: operation cancelled";
                logger.Error(err);
                return (false, err);
            }
            catch (ArgumentNullException ex)
            {
                string err = $"Not unsubscribing Future market data: missing or invalid parameter {ex.ParamName}";
                logger.Error(err);
                return (false, err);
            }
            catch (Exception ex)
            {
                string err = "Failed to unsubscribe to Futures market data";
                logger.Error(err, ex);
                return (false, $"{err}: {ex.Message}");
            }
        }

        public (bool Success, string Message) UnsubscribeFutures()
        {
            var currentlySubscribed = futureMarketDataRequests.Where(r => r.Value.Submitted).Select(r => (r.Value.Contract.Exchange, r.Value.Contract.Symbol, r.Value.Contract.Multiplier));

            if (!currentlySubscribed.IsNullOrEmpty())
                return (true, "Nothing to unsubscribe");
            else
                return UnsubscribeFutures(currentlySubscribed);
        }

        public (bool Success, string Message) ReplaceFuturesSubscriptions(IEnumerable<(string Exchange, string Symbol, double Multiplier)> futures)
        {
            try
            {
                StringBuilder sb = new StringBuilder("");

                var currentlySubscribed = futureMarketDataRequests.Where(r => r.Value.Submitted).Select(r => (r.Value.Contract.Exchange, r.Value.Contract.Symbol, r.Value.Contract.Multiplier));

                if (!currentlySubscribed.IsNullOrEmpty() && futures.IsNullOrEmpty())
                {
                    UnsubscribeFutures(currentlySubscribed);
                    sb.Append($"Unsubscribed all: {string.Join(", ", currentlySubscribed)}");
                }

                var toUnsubscribe = currentlySubscribed.Except(futures);

                if (!toUnsubscribe.IsNullOrEmpty())
                {
                    UnsubscribeFutures(toUnsubscribe);
                    sb.Append($"Newly unsubscribed: {string.Join(", ", toUnsubscribe)}; ");
                }

                var toSubscribe = futures.Except(currentlySubscribed);

                if (!toSubscribe.IsNullOrEmpty())
                {
                    SubscribeFutures(toSubscribe);
                    sb.Append($"Newly subscribed: {string.Join(", ", toSubscribe)}; ");
                }

                return (true, sb.ToString());
            }
            catch (Exception ex)
            {
                string err = "Failed to replace Futures subscriptions";
                logger.Error(err, ex);
                return (false, $"{err}: {ex.Message}");
            }
        }

        public (bool Success, string Message) SubscribeFuturesRTBars(string exchange, string symbol, double multiplier)
        {
            string msg = "Futures RT Bars are not currently supported";
            logger.Warn(msg);
            return (true, msg);
        }

        public (bool Success, string Message) SubscribeFuturesRTBars(IEnumerable<(string Exchange, string Symbol, double Multiplier)> futures)
        {
            string msg = "Futures RT Bars are not currently supported";
            logger.Warn(msg);
            return (true, msg);
        }

        public (bool Success, string Message) UnsubscribeFuturesRTBars(string exchange, string symbol, double multiplier)
        {
            string msg = "Futures RT Bars are not currently supported";
            logger.Warn(msg);
            return (true, msg);
        }

        public (bool Success, string Message) UnsubscribeFuturesRTBars()
        {
            string msg = "Futures RT Bars are not currently supported";
            logger.Warn(msg);
            return (true, msg);
        }

        public (bool Success, string Message) UnsubscribeFuturesRTBars(IEnumerable<(string Exchange, string Symbol, double Multiplier)> futures)
        {
            string msg = "Futures RT Bars are not currently supported";
            logger.Warn(msg);
            return (true, msg);
        }

        public (bool Success, string Message) ReplaceFuturesSubscriptionsRTBars(IEnumerable<(string Exchange, string Symbol, double Multiplier)> futures)
        {
            string msg = "Futures RT Bars are not currently supported";
            logger.Warn(msg);
            return (true, msg);
        }

        public void SubscribeRTBars(IEnumerable<Cross> crosses)
        {
            if (!crosses.IsNullOrEmpty())
            {
                logger.Info($"Subscribing RT bars for {string.Join(", ", crosses)}");

                SubmitMarketDataRequests(GetFXRequestIdsForCrossesAndRequestTypes(crosses, new MarketDataRequestType[3] { MarketDataRequestType.RtBarAsk, MarketDataRequestType.RtBarBid, MarketDataRequestType.RtBarMid }, false));

                foreach (var cross in crosses)
                    currentRtBarsSubscribed.AddOrUpdate(cross, true, (key, oldValue) => true);
            }
        }

        public void SubscribeRTBars(Cross cross)
        {
            logger.Info($"Subscribing RT bars for {cross}");

            SubmitMarketDataRequests(GetFXRequestIdsForCrossAndRequestTypes(cross, new MarketDataRequestType[3] { MarketDataRequestType.RtBarAsk, MarketDataRequestType.RtBarBid, MarketDataRequestType.RtBarMid }, false));

            currentRtBarsSubscribed.AddOrUpdate(cross, true, (key, oldValue) => true);
        }

        public void UnsubscribeMDTicks()
        {
            logger.Info("Unsubscribing all MD ticks");

            CancelMarketDataRequests(GetRequestIdsForRequestType(MarketDataRequestType.MarketDataTick, true));

            currentMdTicksSubscribed.Clear();
        }

        public void UnsubscribeMDTicks(IEnumerable<Cross> crosses)
        {
            if (!crosses.IsNullOrEmpty())
            {
                logger.Info($"Unsubscribing MD ticks for {string.Join(", ", crosses)}");

                CancelMarketDataRequests(GetFXRequestIdsForCrossesAndRequestType(crosses, MarketDataRequestType.MarketDataTick, true));

                foreach (var cross in crosses)
                    currentMdTicksSubscribed.AddOrUpdate(cross, false, (key, oldValue) => false);
            }
        }

        public void UnsubscribeMDTicks(Cross cross)
        {
            logger.Info($"Unsubscribing MD ticks for {cross}");

            CancelMarketDataRequest(GetFXRequestIdForCrossAndRequestType(cross, MarketDataRequestType.MarketDataTick, true));

            currentMdTicksSubscribed.AddOrUpdate(cross, false, (key, oldValue) => false);
        }

        public void UnsubscribeRTBars()
        {
            logger.Info("Unsubscribing all RT bars");

            CancelMarketDataRequests(GetRequestIdsForRequestTypes(new MarketDataRequestType[3] { MarketDataRequestType.RtBarAsk, MarketDataRequestType.RtBarBid, MarketDataRequestType.RtBarMid }, true));

            currentRtBarsSubscribed.Clear();
        }

        public void UnsubscribeRTBars(IEnumerable<Cross> crosses)
        {
            if (!crosses.IsNullOrEmpty())
            {
                logger.Info($"Unsubscribing RT bars for {string.Join(", ", crosses)}");

                CancelMarketDataRequests(GetFXRequestIdsForCrossesAndRequestTypes(crosses, new MarketDataRequestType[3] { MarketDataRequestType.RtBarAsk, MarketDataRequestType.RtBarBid, MarketDataRequestType.RtBarMid }, true));

                foreach (var cross in crosses)
                    currentRtBarsSubscribed.AddOrUpdate(cross, false, (key, oldValue) => false);
            }
        }

        public void UnsubscribeRTBars(Cross cross)
        {
            logger.Info($"Unsubscribing RT bars for {cross}");

            CancelMarketDataRequests(GetFXRequestIdsForCrossAndRequestTypes(cross, new MarketDataRequestType[3] { MarketDataRequestType.RtBarAsk, MarketDataRequestType.RtBarBid, MarketDataRequestType.RtBarMid }, true));

            currentRtBarsSubscribed.AddOrUpdate(cross, false, (key, oldValue) => false);
        }

        private void UnsubscribeAll()
        {
            logger.Info("Unsubscribing all market data requests");

            CancelMarketDataRequests(fxMarketDataRequests.Where(r => r.Value.Submitted).Select(r => r.Key));
            UnsubscribeFutures(futureMarketDataRequests.Where(r => r.Value.Submitted).Select(r => (r.Value.Contract.Exchange, r.Value.Contract.Symbol, r.Value.Contract.Multiplier)));

            currentMdTicksSubscribed.Clear();
            currentRtBarsSubscribed.Clear();
        }

        internal bool HandleHistoricalDataDisconnection()
        {
            // Throttle notifications
            if (DateTimeOffset.Now.Subtract(lastHistoricalDataLostCheck) > TimeSpan.FromSeconds(5))
            {
                lock (lastHistoricalDataLostCheckLocker)
                {
                    lastHistoricalDataLostCheck = DateTimeOffset.Now;
                }

                var subscribed = currentRtBarsSubscribed.ToArray();

                bool needToNotify = subscribed.Count(s => s.Value) > 0;

                if (needToNotify)
                    HistoricalDataConnectionLost?.Invoke();

                return needToNotify;
            }
            else
                return false;
        }

        internal bool HandleHistoricalDataReconnection()
        {
            // Throttle notifications
            if (DateTimeOffset.Now.Subtract(lastHistoricalDataResumedCheck) > TimeSpan.FromSeconds(5))
            {
                lock (lastHistoricalDataResumedCheckLocker)
                {
                    lastHistoricalDataResumedCheck = DateTimeOffset.Now;
                }

                var subscribed = currentRtBarsSubscribed.ToArray();

                bool needToNotify = subscribed.Count(s => s.Value) > 0;

                if (needToNotify)
                    HistoricalDataConnectionResumed?.Invoke();

                return needToNotify;
            }
            else
                return false;
        }

        internal bool HandleMarketDataDisconnection()
        {
            // Throttle notifications
            if (DateTimeOffset.Now.Subtract(lastMarketDataLostCheck) > TimeSpan.FromSeconds(5))
            {
                lock (lastMarketDataLostCheckLocker)
                {
                    lastMarketDataLostCheck = DateTimeOffset.Now;
                }

                bool needToNotify = NeedToNotify();

                if (needToNotify)
                    MarketDataConnectionLost?.Invoke();

                return needToNotify;
            }
            else
                return false;
        }

        internal bool HandleMarketDataReconnection()
        {
            // Throttle notifications
            if (DateTimeOffset.Now.Subtract(lastMarketDataResumedCheck) > TimeSpan.FromSeconds(5))
            {
                lock (lastMarketDataResumedCheckLocker)
                {
                    lastMarketDataResumedCheck = DateTimeOffset.Now;
                }

                bool needToNotify = NeedToNotify();

                if (needToNotify)
                    MarketDataConnectionResumed?.Invoke();

                return needToNotify;
            }
            else
                return false;
        }

        private bool NeedToNotify()
        {
            var tickSubscribed = currentMdTicksSubscribed.ToArray();
            var rtBarsSubscribed = currentRtBarsSubscribed.ToArray();
            var cmeFutsSubscribed = currentFuturesSubscribed.ToArray();

            bool needToNotify = tickSubscribed.Count(s => s.Value) > 0 || rtBarsSubscribed.Count(s => s.Value) > 0 || cmeFutsSubscribed.Count(s => s.Value) > 0;
            return needToNotify;
        }

        public void Dispose()
        {
            logger.Info($"Disposing {nameof(IBMarketDataProvider)}");

            UnsubscribeAll();
        }
    }

    internal class MarketDataRequest<T>
    {
        public int RequestId { get; set; }
        public T Contract { get; private set; }
        public MarketDataRequestType Type { get; private set; }
        public bool Submitted { get; set; }

        public MarketDataRequest(int requestId, T contract, MarketDataRequestType type)
        {
            RequestId = requestId;
            Contract = contract;
            Type = type;
            Submitted = false;
        }

        public override string ToString()
        {
            return $"{RequestId} ({Type})";
        }
    }

    internal class FXMarketDataRequest : MarketDataRequest<Contract>
    {
        public FXMarketDataRequest(int requestId, Contract contract, MarketDataRequestType type) : base(requestId, contract, type)
        {
        }
    }

    internal class FutureMarketDataRequest : MarketDataRequest<FutureContract>
    {
        public FutureMarketDataRequest(int requestId, FutureContract contract, MarketDataRequestType type) : base(requestId, contract, type)
        {
        }
    }

    internal enum MarketDataRequestType
    {
        MarketDataTick,
        RtBarBid, RtBarMid, RtBarAsk
    }
}
