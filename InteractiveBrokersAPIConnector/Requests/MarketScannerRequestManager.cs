using Net.Teirlinck.FX.Data.MarketScannerData;
using Net.Teirlinck.FX.InteractiveBrokersAPI.Extensions;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI.Requests
{
    public class MarketScannerRequestManager
    {
        private IBApi.EClientSocket ClientSocket { get; set; }

        public MarketScannerRequestManager(IBClientRequestsManager requestsManager)
        {
            ClientSocket = requestsManager.ClientSocket;
        }

        /// <summary>
        /// Requests all valid parameters that a scanner subscription can have
        /// </summary>
        public void RequestAllValidMarketScannerParameters()
        {
            ClientSocket.reqScannerParameters();
        }

        /// <summary>
        /// Starts a subscription to market scan results based on the provided parameters
        /// </summary>
        /// <param name="requestID">The request's identifier</param>
        /// <param name="subscription">Summary of the scanner subscription parameters including filters</param>
        public void RequestScannerSubscription(int requestID, ScannerSubscription subscription)
        {
            ClientSocket.reqScannerSubscription(requestID, subscription.ToIBScannerSubscription(), null);
        }

        /// <summary>
        /// Cancels a scanner subscription
        /// </summary>
        /// <param name="requestID">The ID that was specified in the call to RequestScannerSubscription()</param>
        public void CancelScannerSubscription(int requestID)
        {
            ClientSocket.cancelScannerSubscription(requestID);
        }
    }
}
