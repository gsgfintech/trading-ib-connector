using System;
using System.Globalization;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI
{
    public partial class IBClientResponsesManager
    {
        public event Action<int, DateTimeOffset, double, double, double, double> HistoricalBarReceived;
        public event Action<int, DateTimeOffset, DateTimeOffset> HistoricalDataRequestCompleted;

        /// <summary>
        /// Receives the historical data in response to reqHistoricalData()
        /// </summary>
        /// <param name="requestId">The request's identifier</param>
        /// <param name="dateStr">The date-time stamp of the start of the bar. The format is determined by the reqHistoricalData() formatDate parameter 
        /// (either as a yyyyMMdd hh:mm:ss formatted string or as system time according to the request)</param>
        /// <param name="open">The bar opening price</param>
        /// <param name="high">The high price during the time covered by the bar</param>
        /// <param name="low">The low price during the time covered by the bar</param>
        /// <param name="close">The bar closing price</param>
        /// <param name="volume">The volume during the time covered by the bar</param>
        /// <param name="count">When TRADES historical data is returned, represents the number of trades that occurred during the time period the bar covers</param>
        /// <param name="WAP">The weighted average price during the time covered by the bar</param>
        /// <param name="hasGaps">Whether or not there are gaps in the data</param>
        public void historicalData(int requestId, string dateStr, double open, double high, double low, double close, int volume, int count, double WAP, bool hasGaps)
        {
            DateTimeOffset timestamp = DateTimeOffset.ParseExact(dateStr, "yyyyMMdd  HH:mm:ss", null, DateTimeStyles.AssumeLocal);

            HistoricalBarReceived?.Invoke(requestId, timestamp, open, high, low, close);
        }

        public void historicalDataEnd(int reqId, string startStr, string endStr)
        {
            DateTimeOffset lowerBound = DateTimeOffset.ParseExact(startStr, "yyyyMMdd  HH:mm:ss", null, DateTimeStyles.AssumeLocal);
            DateTimeOffset upperBound = DateTimeOffset.ParseExact(endStr, "yyyyMMdd  HH:mm:ss", null, DateTimeStyles.AssumeLocal);

            HistoricalDataRequestCompleted?.Invoke(reqId, lowerBound, upperBound);
        }
    }
}
