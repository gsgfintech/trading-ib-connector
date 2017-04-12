using Capital.GSG.FX.Data.Core.ContractData;
using Capital.GSG.FX.Data.Core.HistoricalData;
using Net.Teirlinck.FX.InteractiveBrokersAPI.Extensions;
using System;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI.Requests
{
    public class HistoricalDataRequestManager
    {
        private IBApi.EClientSocket ClientSocket { get; set; }

        public HistoricalDataRequestManager(IBClientRequestsManager requestsManager)
        {
            ClientSocket = requestsManager.ClientSocket;
        }

        /// <summary>
        /// Requests contracts' historical data. The resulting bars will be returned in through historicalData(). 
        /// When requesting historical data, a finishing time and date is required along with a duration string. For example, having:
        ///     endDateTime: 20130701 23:59:59 GMT
        ///     durationStr: 3 D
        /// will return three days of data counting backwards from July 1st 2013 at 23:59:59 GMT, resulting in all the available bars of the last three days until the date and time specified. It is possible to specify a time zone
        /// </summary>
        /// <param name="requestID">The Id for the request. Must be a unique value. When the data is received, it will be identified by this Id. This is also used when canceling the historical data request</param>
        /// <param name="contract">This class contains attributes used to describe the contract</param>
        /// <param name="endDateTime"></param>
        /// <param name="timeSpan">This is the time span the request will cover</param>
        /// <param name="timeSpanUnit">This is the time span the request will cover</param>
        /// <param name="barSize">Specifies the size of the bars that will be returned (within IB/TWS limits)</param>
        /// <param name="dataType">Determines the nature of data being extracted</param>
        /// <param name="getRegularTradingHoursDataOnly">Determines whether to return all data available during the requested time span, or only data that falls within regular trading hours</param>
        public void RequestHistoricalData(int requestID, Contract contract, DateTimeOffset endDateTime, int timeSpan, HistoricalDataTimeSpanUnit timeSpanUnit, HistoricalDataBarSize barSize,
            HistoricalDataDataType dataType, bool getRegularTradingHoursDataOnly = false)
        {
            string endDateStr = endDateTime.ToString("yyyyMMdd HH:mm:ss");
            string timeSpanStr = $"{timeSpan} {timeSpanUnit.ToCharCode()}";
            string barSizeStr = barSize.ToStrCode();
            string dataTypeStr = dataType.ToStrCode();
            int getRegularTradingHoursDataOnlyInt = getRegularTradingHoursDataOnly ? 1 : 0;

            ClientSocket.reqHistoricalData(requestID, contract.ToIBContract(), endDateStr, timeSpanStr, barSizeStr, dataTypeStr, getRegularTradingHoursDataOnlyInt, 1, null);
        }

        /// <summary>
        /// Cancels a historical data request
        /// </summary>
        /// <param name="requestID">The request's identifier</param>
        public void CancelHistoricalDataRequest(int requestID)
        {
            ClientSocket.cancelHistoricalData(requestID);
        }
    }
}
