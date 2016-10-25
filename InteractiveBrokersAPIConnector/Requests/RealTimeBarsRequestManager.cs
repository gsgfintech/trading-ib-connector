using Capital.GSG.FX.Data.Core.ContractData;
using Net.Teirlinck.FX.InteractiveBrokersAPI.Extensions;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI.Requests
{
    public class RealTimeBarsRequestManager
    {
        private IBApi.EClientSocket ClientSocket { get; set; }

        public RealTimeBarsRequestManager(IBClientRequestsManager requestsManager)
        {
            ClientSocket = requestsManager.ClientSocket;
        }

        /// <summary>
        /// Requests real time bars, which are returned via the RealTimeBarReceived of the responses manager. Currently, only 5-second bars are provided. 
        /// This request is subject to the same pacing restrictions as any historical data request
        /// </summary>
        /// <param name="requestID">The ID for the request. This is also used when canceling the historical data request</param>
        /// <param name="contract">This class contains attributes used to describe the contract</param>
        /// <param name="rtBarDataType">Determines the nature of the data extracted</param>
        /// <param name="getRegularTradingHoursDataOnly">Determines whether to return all data available during the requested time span, or only data that falls within regular trading hours</param>
        public void RequestRealTimeBars(int requestID, Contract contract, string rtBarDataType, bool getRegularTradingHoursDataOnly)
        {
            ClientSocket.reqRealTimeBars(requestID, contract.ToIBContract(), 0, rtBarDataType, getRegularTradingHoursDataOnly, null);
        }

        /// <summary>
        /// Call this method to stop receiving real time bar results
        /// </summary>
        /// <param name="requestID">The ID that was specified</param>
        public void CancelRealTimeBarsRequest(int requestID)
        {
            ClientSocket.cancelRealTimeBars(requestID);
        }
    }
}
