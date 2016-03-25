using static Net.Teirlinck.Utils.DateTimeUtils;
using System;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI
{
    public partial class IBClientResponsesManager
    {
        public event Action<int, DateTime, double, double, double, double, TimeSpan> RealTimeBarReceived;

        /// <summary>
        /// Updates real time 5-second bars
        /// </summary>
        /// <param name="reqId">The request's identifier</param>
        /// <param name="unixTimestamp">The date-time stamp of the start of the bar</param>
        /// <param name="open">The bar opening price</param>
        /// <param name="high">The high price during the time covered by the bar</param>
        /// <param name="low">The low price during the time covered by the bar</param>
        /// <param name="close">The bar closing price</param>
        /// <param name="volume">The volume during the time covered by the bar</param>
        /// <param name="WAP">The weighted average price during the time covered by the bar</param>
        /// <param name="count">When TRADES data is returned, represents the number of trades that occurred during the time period the bar covers</param>
        public void realtimeBar(int reqId, long unixTimestamp, double open, double high, double low, double close, long volume, double WAP, int count)
        {
            if (RealTimeBarReceived != null)
            {
                DateTime time = GetFromUnixTimeStamp(unixTimestamp);

                TimeSpan delay = DateTime.Now.Subtract(time);

                RealTimeBarReceived?.Invoke(reqId, time, open, high, low, close, delay);
            }
        }
    }
}
