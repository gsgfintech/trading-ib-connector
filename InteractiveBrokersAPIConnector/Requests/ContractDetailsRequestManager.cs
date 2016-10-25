using Capital.GSG.FX.Data.Core.ContractData;
using Net.Teirlinck.FX.InteractiveBrokersAPI.Extensions;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI.Requests
{
    public class ContractDetailsRequestManager
    {
        private IBApi.EClientSocket ClientSocket { get; set; }

        public ContractDetailsRequestManager(IBClientRequestsManager requestsManager)
        {
            ClientSocket = requestsManager.ClientSocket;
        }

        /// <summary>
        /// This method returns all contracts matching the contract provided. It can also be used to retrieve complete options and futures chains. 
        /// The contract details will be received via the ContractDetailsReceived event of the responses manager
        /// </summary>
        /// <param name="requestID"></param>
        /// <param name="contract"></param>
        public void RequestContractDetails(int requestID, Contract contract)
        {
            ClientSocket.reqContractDetails(requestID, contract.ToIBContract());
        }
    }
}
