using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using log4net;
using System.Collections.Concurrent;
using System.Threading;
using Capital.GSG.FX.Data.Core.ContractData;
using Capital.GSG.FX.Data.Core.MarketData;
using Capital.GSG.FX.Utils.Core;
using Capital.GSG.FX.Data.Core.HistoricalData;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI.Executor
{
    public class IBHistoricalDataProvider
    {
        private readonly ILog logger = LogManager.GetLogger(nameof(IBHistoricalDataProvider));

        private Dictionary<Tuple<Cross, int, HistoricalDataTimeSpanUnit, HistoricalDataBarSize>, int[]> historicalDataRequestsByCross;
        private Dictionary<int, HistoricalDataRequest> historicalDataRequestsById;

        private readonly IBClient ibClient;
        private readonly CancellationToken stopRequestedCt;

        public IBHistoricalDataProvider(IBClient ibClient, IEnumerable<Contract> ibContracts, CancellationToken stopRequestedCt)
        {
            this.stopRequestedCt = stopRequestedCt;
            this.ibClient = ibClient;

            SetupHistoricalDataRequests(ibContracts);
        }

        private void SetupHistoricalDataRequests(IEnumerable<Contract> ibContracts)
        {
            if (!ibContracts.IsNullOrEmpty())
            {
                historicalDataRequestsByCross = new Dictionary<Tuple<Cross, int, HistoricalDataTimeSpanUnit, HistoricalDataBarSize>, int[]>();
                historicalDataRequestsById = new Dictionary<int, HistoricalDataRequest>();

                int counter = 1000;
                foreach (Contract contract in ibContracts)
                {
                    int askId = counter + 1;
                    int midId = counter + 2;
                    int bidId = counter + 3;

                    historicalDataRequestsByCross.Add(new Tuple<Cross, int, HistoricalDataTimeSpanUnit, HistoricalDataBarSize>(contract.Cross, 3600, HistoricalDataTimeSpanUnit.SECONDS, HistoricalDataBarSize.FIVE_SECONDS), new int[3] { askId, midId, bidId });

                    historicalDataRequestsById.Add(askId, new HistoricalDataRequest(askId, contract, HistoricalDataDataType.ASK, 3600, HistoricalDataTimeSpanUnit.SECONDS, HistoricalDataBarSize.FIVE_SECONDS));
                    historicalDataRequestsById.Add(midId, new HistoricalDataRequest(midId, contract, HistoricalDataDataType.MIDPOINT, 3600, HistoricalDataTimeSpanUnit.SECONDS, HistoricalDataBarSize.FIVE_SECONDS));
                    historicalDataRequestsById.Add(bidId, new HistoricalDataRequest(bidId, contract, HistoricalDataDataType.BID, 3600, HistoricalDataTimeSpanUnit.SECONDS, HistoricalDataBarSize.FIVE_SECONDS));

                    counter += 1000;
                }
            }
        }

        public async Task<(List<RTBar> Bars, DateTimeOffset? LowerBound, DateTimeOffset? UpperBound)> Retrieve5SecondsHistoricalBars(Cross cross, DateTimeOffset lowerBound, DateTimeOffset? upperBound = null)
        {
            if (historicalDataRequestsByCross.TryGetValue(new Tuple<Cross, int, HistoricalDataTimeSpanUnit, HistoricalDataBarSize>(cross, 3600, HistoricalDataTimeSpanUnit.SECONDS, HistoricalDataBarSize.FIVE_SECONDS), out int[] requestIds))
            {
                var askRequest = historicalDataRequestsById[requestIds[0]];
                var midRequest = historicalDataRequestsById[requestIds[1]];
                var bidRequest = historicalDataRequestsById[requestIds[2]];

                using (var runner = new HistoricalDataRequestRunner(ibClient, cross, askRequest, midRequest, bidRequest))
                {
                    return await runner.Request5SecondsHistoricalBars(lowerBound, upperBound);
                }
            }
            else
                logger.Error($"Unable to retrieve 5 seconds historical bars for {cross}: the request is not listed (most likely because the IB contract is unknown)");

            return (null, null, null);
        }

        private class HistoricalDataRequestRunner : IDisposable
        {
            private readonly IBClient ibClient;

            private readonly Cross cross;

            private readonly HistoricalDataRequest askRequest;
            private readonly HistoricalDataRequest midRequest;
            private readonly HistoricalDataRequest bidRequest;

            private readonly ConcurrentDictionary<DateTimeOffset, RTBar> rtBars = new ConcurrentDictionary<DateTimeOffset, RTBar>();

            private readonly AutoResetEvent requestCompletedEvent = new AutoResetEvent(false);

            private DateTimeOffset? resultLowerBound;
            private DateTimeOffset? resultUpperBound;

            public HistoricalDataRequestRunner(IBClient ibClient, Cross cross, HistoricalDataRequest askRequest, HistoricalDataRequest midRequest, HistoricalDataRequest bidRequest)
            {
                this.ibClient = ibClient;

                this.cross = cross;

                this.askRequest = askRequest;
                this.midRequest = midRequest;
                this.bidRequest = bidRequest;

                this.ibClient.ResponseManager.HistoricalBarReceived += HistoricalBarReceived; ;
                this.ibClient.ResponseManager.HistoricalDataRequestCompleted += HistoricalDataRequestCompleted;
            }

            private void HistoricalBarReceived(int requestId, DateTimeOffset timestamp, double open, double high, double low, double close)
            {
                if (requestId != askRequest.RequestId && requestId != midRequest.RequestId && requestId != bidRequest.RequestId)
                    return; // We're not interested in this historical bar

                RTBarPoint point = new RTBarPoint()
                {
                    Close = close,
                    Cross = cross,
                    High = high,
                    Low = low,
                    Open = open,
                    Timestamp = timestamp
                };

                rtBars.AddOrUpdate(timestamp, (key) =>
                {
                    RTBar bar = new RTBar()
                    {
                        Cross = cross,
                        Timestamp = timestamp
                    };

                    if (requestId == askRequest.RequestId)
                        bar.Ask = point;
                    else if (requestId == midRequest.RequestId)
                        bar.Mid = point;
                    else
                        bar.Bid = point;

                    return bar;
                }, (key, oldValue) =>
                {
                    if (requestId == askRequest.RequestId)
                        oldValue.Ask = point;
                    else if (requestId == midRequest.RequestId)
                        oldValue.Mid = point;
                    else
                        oldValue.Bid = point;

                    return oldValue;
                });
            }

            private void HistoricalDataRequestCompleted(int requestId, DateTimeOffset lowerBound, DateTimeOffset upperBound)
            {
                resultLowerBound = lowerBound;
                resultUpperBound = upperBound;

                requestCompletedEvent.Set();
            }

            public async Task<(List<RTBar> Bars, DateTimeOffset? LowerBound, DateTimeOffset? UpperBound)> Request5SecondsHistoricalBars(DateTimeOffset lowerBound, DateTimeOffset? upperBound)
            {
                return await Task.Run(() =>
                {
                    int timespan = upperBound.HasValue ? Math.Min(askRequest.Timespan, (int)upperBound.Value.Subtract(lowerBound).TotalSeconds) : askRequest.Timespan;
                    HistoricalDataTimeSpanUnit timespanUnit = HistoricalDataTimeSpanUnit.SECONDS;

                    // 1. Ask
                    ibClient.RequestManager.HistoricalDataRequestManager.RequestHistoricalData(askRequest.RequestId, askRequest.Contract, upperBound ?? lowerBound.AddSeconds(timespan), timespan, timespanUnit, askRequest.BarSize, askRequest.Type);

                    Task.Delay(TimeSpan.FromSeconds(1)).Wait();

                    // 2. Mid                                                                                                                                   
                    ibClient.RequestManager.HistoricalDataRequestManager.RequestHistoricalData(midRequest.RequestId, midRequest.Contract, upperBound ?? lowerBound.AddSeconds(timespan), timespan, timespanUnit, midRequest.BarSize, midRequest.Type);

                    Task.Delay(TimeSpan.FromSeconds(1)).Wait();

                    // 3. Bid                                                                                                                                   
                    ibClient.RequestManager.HistoricalDataRequestManager.RequestHistoricalData(bidRequest.RequestId, bidRequest.Contract, upperBound ?? lowerBound.AddSeconds(timespan), timespan, timespanUnit, bidRequest.BarSize, bidRequest.Type);

                    Task.Delay(TimeSpan.FromSeconds(1)).Wait();

                    requestCompletedEvent.WaitOne(TimeSpan.FromSeconds(30));

                    return (rtBars.ToArray().Select(kvp => kvp.Value).OrderBy(b => b.Timestamp).ToList(), resultLowerBound, resultUpperBound);
                });
            }

            public void Dispose()
            {
                ibClient.ResponseManager.HistoricalBarReceived -= HistoricalBarReceived; ;
                ibClient.ResponseManager.HistoricalDataRequestCompleted -= HistoricalDataRequestCompleted;
            }
        }

        private class HistoricalDataRequest
        {
            public int RequestId { get; private set; }
            public Contract Contract { get; private set; }
            public HistoricalDataDataType Type { get; private set; }

            public int Timespan { get; private set; }
            public HistoricalDataTimeSpanUnit TimespanUnit { get; private set; }
            public HistoricalDataBarSize BarSize { get; private set; }

            public HistoricalDataRequest(int requestId, Contract contract, HistoricalDataDataType type, int timespan, HistoricalDataTimeSpanUnit timespanUnit, HistoricalDataBarSize barSize)
            {
                RequestId = requestId;
                Contract = contract;
                Type = type;
                Timespan = timespan;
                TimespanUnit = timespanUnit;
                BarSize = barSize;
            }
        }
    }
}
