using System;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI
{
    public partial class IBClientResponsesManager
    {
        public event Func<int, string, string> FundamentalDataReceived;

        /// <summary>
        /// This method is called to receive Reuters global fundamental market data. 
        /// There must be a subscription to Reuters Fundamental set up in Account Management before you can receive this data
        /// </summary>
        /// <param name="reqId">The request's identifier</param>
        /// <param name="data">One of these XML reports:
        ///     * Company overview
        ///     * Financial summary
        ///     * Financial ratios
        ///     * Financial statements
        ///     * Analyst estimates
        ///     * Company calendar</param>
        public void fundamentalData(int reqId, string data)
        {
            FundamentalDataReceived?.Invoke(reqId, data);
        }
    }
}
