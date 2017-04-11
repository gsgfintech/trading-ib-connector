using Capital.GSG.FX.Data.Core.ContractData;
using Net.Teirlinck.FX.InteractiveBrokersAPI.Extensions;
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

        /// <summary>
        /// Returns beginning of data for contract for specified data type. In response to EClient::reqHeadTimestamp
        /// </summary>
        /// <param name="reqId"></param>
        /// <param name="headTimestamp">string identifying earliest data date</param>
        public void headTimestamp(int reqId, string headTimestamp)
        {
            // TODO
        }

        /// <summary>
        /// Returns array of sample contract descriptions. In response to EClient::reqMatchingSymbols
        /// </summary>
        /// <param name="reqId"></param>
        /// <param name="contractDescriptions"></param>
        public void symbolSamples(int reqId, IBApi.ContractDescription[] contractDescriptions)
        {
            // TODO
        }

        /// <summary>
        /// Returns array of family codes. In response to EClient::reqFamilyCodes
        /// </summary>
        /// <param name="familyCodes"></param>
        public void familyCodes(IBApi.FamilyCode[] familyCodes)
        {
            // TODO
        }
    }
}
