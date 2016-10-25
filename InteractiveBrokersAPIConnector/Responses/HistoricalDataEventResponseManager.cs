using Capital.GSG.FX.Data.Core.HistoricalData;
using System;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI
{
    public partial class IBClientResponsesManager
    {
        public event Func<int, string, double, double, double, double, int, int, double, bool, HistoricalDataTick> HistoricalDataTickReceived;
        public event Action<int, string, string> HistoricalDataRequestEnd;

        /// <summary>
        /// Receives the historical data in response to reqHistoricalData()
        /// </summary>
        /// <param name="reqId">The request's identifier</param>
        /// <param name="date">The date-time stamp of the start of the bar. The format is determined by the reqHistoricalData() formatDate parameter 
        /// (either as a yyyymmss hh:mm:ss formatted string or as system time according to the request)</param>
        /// <param name="open">The bar opening price</param>
        /// <param name="high">The high price during the time covered by the bar</param>
        /// <param name="low">The low price during the time covered by the bar</param>
        /// <param name="close">The bar closing price</param>
        /// <param name="volume">The volume during the time covered by the bar</param>
        /// <param name="count">When TRADES historical data is returned, represents the number of trades that occurred during the time period the bar covers</param>
        /// <param name="WAP">The weighted average price during the time covered by the bar</param>
        /// <param name="hasGaps">Whether or not there are gaps in the data</param>
        public void historicalData(int reqId, string date, double open, double high, double low, double close, int volume, int count, double WAP, bool hasGaps)
        {
            HistoricalDataTickReceived?.Invoke(reqId, date, open, high, low, close, volume, count, WAP, hasGaps);
        }

        public void historicalDataEnd(int reqId, string start, string end)
        {
            HistoricalDataRequestEnd?.Invoke(reqId, start, end);
        }
    }
}
