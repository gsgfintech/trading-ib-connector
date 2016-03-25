using Net.Teirlinck.FX.InteractiveBrokersAPI.Extensions;
using Net.Teirlinck.FX.Data.ContractData;
using System;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI
{
    public partial class IBClientResponsesManager
    {
        public event Func<int, ContractDetails, ContractDetails> BondContractDetailsReceived;
        public event Func<int, ContractDetails, ContractDetails> ContractDetailsReceived;
        public event Action<int> ContractDetailsRequestEnded;

        /// <summary>
        /// Sends bond contract data when the reqContractDetails() method has been called for bonds.
        /// </summary>
        /// <param name="reqId">The ID of the data request</param>
        /// <param name="contractDetails">This structure contains a full description of the bond contract being looked up</param>
        public void bondContractDetails(int reqId, IBApi.ContractDetails contractDetails)
        {
            BondContractDetailsReceived?.Invoke(reqId, contractDetails.ToContractDetails());
        }

        /// <summary>
        /// Returns all contracts matching the requested parameters in reqContractDetails(). For example, you can receive an entire option chain
        /// </summary>
        /// <param name="reqId">The ID of the data request. Ensures that responses are matched to requests if several requests are in process</param>
        /// <param name="contractDetails">This structure contains a full description of the contract being looked up</param>
        public void contractDetails(int reqId, IBApi.ContractDetails contractDetails)
        {
            ContractDetailsReceived?.Invoke(reqId, contractDetails.ToContractDetails());
        }

        /// <summary>
        /// This method is called once all contract details for a given request are received. This helps to define the end of an option chain.
        /// </summary>
        /// <param name="reqId">The Id of the data request</param>
        public void contractDetailsEnd(int reqId)
        {
            ContractDetailsRequestEnded?.Invoke(reqId);
        }
    }
}
