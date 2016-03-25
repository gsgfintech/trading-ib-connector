using Net.Teirlinck.FX.Data.ContractData;
using Net.Teirlinck.FX.Data.ExecutionData;
using Net.Teirlinck.FX.InteractiveBrokersAPI.Extensions;
using System;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI
{
    public partial class IBClientResponsesManager
    {
        public event Action<CommissionReport> CommissionReportReceived;
        public event Action<int, Contract, Execution> ExecutionDetailsReceived;
        public event Action<int> ExecutionDetailsRequestEnded;

        /// <summary>
        /// This callback returns the commission report portion of an execution and is triggered immediately after a trade execution, or by calling reqExecution().
        /// </summary>
        /// <param name="ibCommissionReport">The structure that contains commission details</param>
        public void commissionReport(IBApi.CommissionReport ibCommissionReport)
        {
            CommissionReportReceived?.Invoke(ibCommissionReport.ToCommissionReport());
        }

        /// <summary>
        /// Returns executions from the last 24 hours as a response to reqExecutions(), or when an order is filled
        /// </summary>
        /// <param name="reqId">The request's identifier</param>
        /// <param name="ibContract">This structure contains a full description of the contract that was executed</param>
        /// <param name="ibExecution">This structure contains addition order execution details</param>
        public void execDetails(int reqId, IBApi.Contract ibContract, IBApi.Execution ibExecution)
        {
            Contract contract = ibContract.ToContract();
            ExecutionDetailsReceived?.Invoke(reqId, contract, ibExecution.ToExecution(contract.Cross));
        }

        /// <summary>
        /// This method is called once all executions have been sent to a client in response to reqExecutions()
        /// </summary>
        /// <param name="reqId">The request's identifier</param>
        public void execDetailsEnd(int reqId)
        {
            ExecutionDetailsRequestEnded?.Invoke(reqId);
        }
    }
}
