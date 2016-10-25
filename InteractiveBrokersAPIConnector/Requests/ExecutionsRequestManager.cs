using Capital.GSG.FX.Data.Core.ExecutionData;
using Net.Teirlinck.FX.InteractiveBrokersAPI.Extensions;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI.Requests
{
    public class ExecutionsRequestManager
    {
        private IBApi.EClientSocket ClientSocket { get; set; }

        public ExecutionsRequestManager(IBClientRequestsManager requestsManager)
        {
            ClientSocket = requestsManager.ClientSocket;
        }

        /// <summary>
        /// Requests all the day's executions matching the filter criteria. Only the current day's executions can be retrieved. 
        /// Along with the executions, the CommissionReport will also be returned. Execution details are returned to the client via execDetails(). 
        /// To view executions beyond the past 24 hours, open the Trade Log in TWS and, while the Trade Log is displayed, request the executions again from the API
        /// </summary>
        /// <param name="requestID">The request's unique identifier</param>
        /// <param name="filter">The filter criteria used to determine which execution reports are returned</param>
        public void RequestTodaysExecutions(int requestID, ExecutionFilter filter)
        {
            IBApi.ExecutionFilter ibExecFilter = filter.ToIBExecutionFilter();
            ClientSocket.reqExecutions(requestID, ibExecFilter);
        }
    }
}
