using Capital.GSG.FX.Trading.Executor;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Net.Teirlinck.FX.Data.ContractData;
using log4net;
using System.Collections.Concurrent;
using Net.Teirlinck.FX.Data.MarketData;
using static Net.Teirlinck.FX.Data.MarketData.MarketDataTickType;
using System.Threading;
using Net.Teirlinck.Utils;
using static Net.Teirlinck.FX.Data.System.SystemStatusLevel;
using Capital.GSG.FX.IBData.Service.Connector;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI.Executor
{
    public class IBMarketDataProvider : IMarketDataProvider
    {
        private static ILog logger = LogManager.GetLogger(nameof(IBMarketDataProvider));

        private static IBMarketDataProvider _instance;

        private readonly Dictionary<int, MarketDataRequest> marketDataRequests = new Dictionary<int, MarketDataRequest>();

        private readonly ConcurrentDictionary<Cross, RTBar> currentRtBars = new ConcurrentDictionary<Cross, RTBar>();

        private readonly MarketDataTickType[] interestingSizeTickTypes = { BID_SIZE, ASK_SIZE };
        private readonly MarketDataTickType[] interestingPriceTickTypes = { BID, ASK };

        private readonly BrokerClient brokerClient;
        private readonly IBClient ibClient;
        private readonly ContractsConnector contractsConnector;
        private readonly bool logTicks;
        private readonly CancellationToken stopRequestedCt;

        private IBMarketDataProvider(BrokerClient brokerClient, IBClient ibClient, string ibDataServiceEndpoint, bool logTicks, CancellationToken stopRequestedCt)
        {
            if (brokerClient == null)
                throw new ArgumentNullException(nameof(brokerClient));

            if (ibClient == null)
                throw new ArgumentNullException(nameof(ibClient));

            if (string.IsNullOrEmpty(ibDataServiceEndpoint))
                throw new ArgumentNullException(nameof(ibDataServiceEndpoint));

            this.brokerClient = brokerClient;

            contractsConnector = ContractsConnector.GetConnector(ibDataServiceEndpoint);

            this.stopRequestedCt = stopRequestedCt;

            this.ibClient = ibClient;

            this.ibClient.IBConnectionLost += () =>
            {
                logger.Error("IB client disconnected from TWS. Clearing current RT bars cache, if any");

                currentRtBars.Clear();
            };

            this.ibClient.IBConnectionEstablished += () =>
            {
                logger.Info("IB client (re)connected. Will resubmit MD ticks and RT bars requests, if any");

                ResubmitPreviousMarketDataRequests();
            };

            this.logTicks = logTicks;

            SetupEventListeners();
        }

        internal static async Task<IBMarketDataProvider> SetupIBMarketDataProvider(BrokerClient brokerClient, IBClient ibClient, string ibDataServiceEndpoint, bool logTicks, CancellationToken stopRequestedCt)
        {
            _instance = new IBMarketDataProvider(brokerClient, ibClient, ibDataServiceEndpoint, logTicks, stopRequestedCt);

            await _instance.LoadContracts();

            return _instance;
        }

        private async Task LoadContracts()
        {
            List<Contract> list = await contractsConnector.GetAll(stopRequestedCt);

            if (!list.IsNullOrEmpty())
            {
                marketDataRequests.Clear();

                int counter = 10;
                foreach (Contract contract in list)
                {
                    marketDataRequests.Add(counter + 0, new MarketDataRequest(counter + 0, contract.Cross, contract, MarketDataRequestType.MarketDataTick));
                    marketDataRequests.Add(counter + 1, new MarketDataRequest(counter + 1, contract.Cross, contract, MarketDataRequestType.RtBarBid));
                    marketDataRequests.Add(counter + 2, new MarketDataRequest(counter + 2, contract.Cross, contract, MarketDataRequestType.RtBarMid));
                    marketDataRequests.Add(counter + 3, new MarketDataRequest(counter + 3, contract.Cross, contract, MarketDataRequestType.RtBarAsk));

                    counter += 10;
                }
            }
        }

        private void SetupEventListeners()
        {
            ibClient.ResponseManager.MarketDataSizeTickReceived += ResponseManager_MarketDataSizeTickReceived;
            ibClient.ResponseManager.MarketDataPriceTickReceived += ResponseManager_MarketDataPriceTickReceived;
            ibClient.ResponseManager.RealTimeBarReceived += ResponseManager_RealTimeBarReceived;
        }

        private void ResponseManager_MarketDataSizeTickReceived(int requestID, MarketDataTickType tickType, int size)
        {
            if (marketDataRequests.ContainsKey(requestID) && marketDataRequests[requestID].Type == MarketDataRequestType.MarketDataTick)
            {
                Cross cross = marketDataRequests[requestID].Cross;

                if (size > 0 && interestingSizeTickTypes.Contains(tickType))
                {
                    if (logTicks)
                        logger.Debug($"Received size tick data: requestID={requestID}, cross={cross}, tickType={tickType}, size={size}");

                    brokerClient.TradingExecutorRunner?.OnMdSizeTick(new SizeTick(cross, tickType, size));
                }
                else
                    logger.Debug($"Received size tick data with 0 size. Not recording: requestID={requestID}, cross={cross}, tickType={tickType}, size={size}");
            }
        }

        private void ResponseManager_MarketDataPriceTickReceived(int requestID, MarketDataTickType tickType, double value, bool canAutoExecute)
        {
            if (marketDataRequests.ContainsKey(requestID) && marketDataRequests[requestID].Type == MarketDataRequestType.MarketDataTick)
            {
                Cross cross = marketDataRequests[requestID].Cross;

                if (value > 0.0 && interestingPriceTickTypes.Contains(tickType))
                {
                    if (logTicks)
                        logger.Debug($"Received price tick data: requestID={requestID}, cross={cross}, type={tickType}, value={value}, canAutoExecute={canAutoExecute}");

                    brokerClient.TradingExecutorRunner?.OnMdPriceTick(new PriceTick(cross, tickType, value, canAutoExecute));
                }
                else
                    logger.Debug($"Received price tick data with 0.0 value. Not recording: requestID={requestID}, cross={cross}, type={tickType}, value={value}, canAutoExecute={canAutoExecute}");
            }
        }

        private void ResponseManager_RealTimeBarReceived(int requestID, DateTime time, double open, double high, double low, double close, TimeSpan delay)
        {
            if (marketDataRequests.ContainsKey(requestID) && marketDataRequests[requestID].Type != MarketDataRequestType.MarketDataTick)
            {
                Cross cross = marketDataRequests[requestID].Cross;

                double delayInMs = Math.Round(delay.TotalMilliseconds, 0);

                if (logTicks)
                    logger.Debug($"Received real time bar: requestID={requestID}, cross={cross}, type={marketDataRequests[requestID].Type}, time={time}, open={open}, high={high}, low={low}, close={close}, delay={delayInMs:N0}ms");

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

                        switch (marketDataRequests[requestID].Type)
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

                        switch (marketDataRequests[requestID].Type)
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

                    switch (marketDataRequests[requestID].Type)
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
            }
        }

        private int GetRequestIdForCrossAndRequestType(Cross cross, MarketDataRequestType type, bool submitted)
        {
            return marketDataRequests.Where(r => r.Value.Cross == cross && r.Value.Type == type && r.Value.Submitted == submitted).Select(r => r.Key).FirstOrDefault();
        }

        internal MarketDataRequest GetRequestDetails(int requestId)
        {
            if (marketDataRequests.ContainsKey(requestId))
                return marketDataRequests[requestId];
            else
            {
                logger.Error($"Unable to retrieve details for unknown requestId {requestId}");
                return null;
            }
        }

        private IEnumerable<int> GetRequestIdsForCrossAndRequestTypes(Cross cross, IEnumerable<MarketDataRequestType> types, bool submitted)
        {
            if (types.IsNullOrEmpty())
                return null;
            else
                return marketDataRequests.Where(r => r.Value.Cross == cross && types.Contains(r.Value.Type) && r.Value.Submitted == submitted).Select(r => r.Key);
        }

        private IEnumerable<int> GetRequestIdsForRequestTypes(IEnumerable<MarketDataRequestType> types, bool submitted)
        {
            if (types.IsNullOrEmpty())
                return null;
            else
                return marketDataRequests.Where(r => types.Contains(r.Value.Type) && r.Value.Submitted == submitted).Select(r => r.Key);
        }

        private IEnumerable<int> GetRequestIdsForCrossesAndRequestType(IEnumerable<Cross> crosses, MarketDataRequestType type, bool submitted)
        {
            if (crosses.IsNullOrEmpty())
                return null;
            else
                return marketDataRequests.Where(r => crosses.Contains(r.Value.Cross) && r.Value.Type == type && r.Value.Submitted == submitted).Select(r => r.Key);
        }

        private IEnumerable<int> GetRequestIdsForRequestType(MarketDataRequestType type, bool submitted)
        {
            return GetRequestIdsForRequestTypes(new MarketDataRequestType[1] { type }, submitted);
        }

        private IEnumerable<int> GetRequestIdsForCrossesAndRequestTypes(IEnumerable<Cross> crosses, IEnumerable<MarketDataRequestType> types, bool submitted)
        {
            if (crosses.IsNullOrEmpty() || types.IsNullOrEmpty())
                return null;
            else
                return marketDataRequests.Where(r => crosses.Contains(r.Value.Cross) && types.Contains(r.Value.Type) && r.Value.Submitted == submitted).Select(r => r.Key);
        }

        private void MarkAllRequestsAsUnsubmitted()
        {
            foreach (KeyValuePair<int, MarketDataRequest> request in marketDataRequests)
                request.Value.Submitted = false;
        }

        private void ResubmitPreviousMarketDataRequests()
        {
            int[] requestIds = marketDataRequests.Where(r => r.Value.Submitted).Select(r => r.Key).Distinct().ToArray();

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
            if (marketDataRequests.ContainsKey(requestId))
            {
                logger.Info($"Submitting market data request {requestId} ({marketDataRequests[requestId].Cross} - {marketDataRequests[requestId].Type})");

                switch (marketDataRequests[requestId].Type)
                {
                    case MarketDataRequestType.MarketDataTick:
                        ibClient.RequestManager.MarketDataRequestManager.RequestMarketData(requestId, marketDataRequests[requestId].Contract);
                        break;
                    case MarketDataRequestType.RtBarBid:
                        ibClient.RequestManager.RealTimeBarsRequestManager.RequestRealTimeBars(requestId, marketDataRequests[requestId].Contract, "BID", true);
                        break;
                    case MarketDataRequestType.RtBarMid:
                        ibClient.RequestManager.RealTimeBarsRequestManager.RequestRealTimeBars(requestId, marketDataRequests[requestId].Contract, "MIDPOINT", true);
                        break;
                    case MarketDataRequestType.RtBarAsk:
                        ibClient.RequestManager.RealTimeBarsRequestManager.RequestRealTimeBars(requestId, marketDataRequests[requestId].Contract, "ASK", true);
                        break;
                    default:
                        break;
                }

                marketDataRequests[requestId].Submitted = true;

                Task.Delay(TimeSpan.FromSeconds(1)).Wait();
            }
            else
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
            if (marketDataRequests.ContainsKey(requestId))
            {
                logger.Info($"Requesting cancellation of market data request {requestId} ({marketDataRequests[requestId].Cross} - {marketDataRequests[requestId].Type})");

                switch (marketDataRequests[requestId].Type)
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

                marketDataRequests[requestId].Submitted = false;
            }
            else
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
            ReplaceMarketDataRequests(crosses, new MarketDataRequestType[1] { MarketDataRequestType.MarketDataTick });
        }

        public void ReplaceRTBarsSubscriptions(IEnumerable<Cross> crosses)
        {
            ReplaceMarketDataRequests(crosses, new MarketDataRequestType[3] { MarketDataRequestType.RtBarAsk, MarketDataRequestType.RtBarBid, MarketDataRequestType.RtBarMid });
        }

        private void ReplaceMarketDataRequests(IEnumerable<Cross> crosses, IEnumerable<MarketDataRequestType> types)
        {
            if (crosses.IsNullOrEmpty())
                CancelMarketDataRequests(GetRequestIdsForRequestTypes(types, true));
            else
            {
                // 1. Cancel requests that were removed
                CancelMarketDataRequests(marketDataRequests.Where(r => !crosses.Contains(r.Value.Cross) && types.Contains(r.Value.Type) && r.Value.Submitted)?.Select(r => r.Key));

                // 2. Subscribe newly added requests
                SubmitMarketDataRequests(marketDataRequests.Where(r => crosses.Contains(r.Value.Cross) && types.Contains(r.Value.Type) && !r.Value.Submitted)?.Select(r => r.Key));
            }
        }

        public void SubscribeMDTicks(IEnumerable<Cross> crosses)
        {
            if (!crosses.IsNullOrEmpty())
            {
                logger.Info($"Subscribing MD ticks for {string.Join(", ", crosses)}");

                SubmitMarketDataRequests(GetRequestIdsForCrossesAndRequestType(crosses, MarketDataRequestType.MarketDataTick, false));
            }
        }

        public void SubscribeMDTicks(Cross cross)
        {
            logger.Info($"Subscribing MD ticks for {cross}");

            SubmitMarketDataRequest(GetRequestIdForCrossAndRequestType(cross, MarketDataRequestType.MarketDataTick, false));
        }

        public void SubscribeRTBars(IEnumerable<Cross> crosses)
        {
            if (!crosses.IsNullOrEmpty())
            {
                logger.Info($"Subscribing RT bars for {string.Join(", ", crosses)}");

                SubmitMarketDataRequests(GetRequestIdsForCrossesAndRequestTypes(crosses, new MarketDataRequestType[3] { MarketDataRequestType.RtBarAsk, MarketDataRequestType.RtBarBid, MarketDataRequestType.RtBarMid }, false));
            }
        }

        public void SubscribeRTBars(Cross cross)
        {
            logger.Info($"Subscribing RT bars for {cross}");

            SubmitMarketDataRequests(GetRequestIdsForCrossAndRequestTypes(cross, new MarketDataRequestType[3] { MarketDataRequestType.RtBarAsk, MarketDataRequestType.RtBarBid, MarketDataRequestType.RtBarMid }, false));
        }

        public void UnsubscribeMDTicks()
        {
            logger.Info("Unsubscribing all MD ticks");

            CancelMarketDataRequests(GetRequestIdsForRequestType(MarketDataRequestType.MarketDataTick, true));
        }

        public void UnsubscribeMDTicks(IEnumerable<Cross> crosses)
        {
            if (!crosses.IsNullOrEmpty())
            {
                logger.Info($"Unsubscribing MD ticks for {string.Join(", ", crosses)}");

                CancelMarketDataRequests(GetRequestIdsForCrossesAndRequestType(crosses, MarketDataRequestType.MarketDataTick, true));
            }
        }

        public void UnsubscribeMDTicks(Cross cross)
        {
            logger.Info($"Unsubscribing MD ticks for {cross}");

            CancelMarketDataRequest(GetRequestIdForCrossAndRequestType(cross, MarketDataRequestType.MarketDataTick, true));
        }

        public void UnsubscribeRTBars()
        {
            logger.Info("Unsubscribing all RT bars");

            CancelMarketDataRequests(GetRequestIdsForRequestTypes(new MarketDataRequestType[3] { MarketDataRequestType.RtBarAsk, MarketDataRequestType.RtBarBid, MarketDataRequestType.RtBarMid }, true));
        }

        public void UnsubscribeRTBars(IEnumerable<Cross> crosses)
        {
            if (!crosses.IsNullOrEmpty())
            {
                logger.Info($"Unsubscribing RT bars for {string.Join(", ", crosses)}");

                CancelMarketDataRequests(GetRequestIdsForCrossesAndRequestTypes(crosses, new MarketDataRequestType[3] { MarketDataRequestType.RtBarAsk, MarketDataRequestType.RtBarBid, MarketDataRequestType.RtBarMid }, true));
            }
        }

        public void UnsubscribeRTBars(Cross cross)
        {
            logger.Info($"Unsubscribing RT bars for {cross}");

            CancelMarketDataRequests(GetRequestIdsForCrossAndRequestTypes(cross, new MarketDataRequestType[3] { MarketDataRequestType.RtBarAsk, MarketDataRequestType.RtBarBid, MarketDataRequestType.RtBarMid }, true));
        }

        private void UnsubscribeAll()
        {
            logger.Info("Unsubscribing all market data requests");
            CancelMarketDataRequests(marketDataRequests.Where(r => r.Value.Submitted).Select(r => r.Key));
        }

        public void Dispose()
        {
            logger.Info($"Disposing {nameof(IBMarketDataProvider)}");

            UnsubscribeAll();
        }
    }

    internal class MarketDataRequest
    {
        public int RequestId { get; set; }
        public Cross Cross { get; private set; }
        public Contract Contract { get; private set; }
        public MarketDataRequestType Type { get; private set; }
        public bool Submitted { get; set; }

        public MarketDataRequest(int requestId, Cross cross, Contract contract, MarketDataRequestType type)
        {
            RequestId = requestId;
            Cross = cross;
            Contract = contract;
            Type = type;
            Submitted = false;
        }

        public override string ToString()
        {
            return $"{RequestId} ({Cross} - {Type})";
        }
    }

    internal enum MarketDataRequestType
    {
        MarketDataTick,
        RtBarBid, RtBarMid, RtBarAsk
    }
}
