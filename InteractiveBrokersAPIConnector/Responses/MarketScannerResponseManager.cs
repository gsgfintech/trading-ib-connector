using Net.Teirlinck.FX.Data.ContractData;
using Net.Teirlinck.FX.InteractiveBrokersAPI.Extensions;
using System;
using Net.Teirlinck.FX.Data.MarketScannerData;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI
{
    public partial class IBClientResponsesManager
    {
        public event Func<int, int, ContractDetails, string, string, string, string, ScannerData> ScannerDataReceived;
        public event Action<int> ScannerRequestEnded;
        public event Func<string, string> ScannerParametersReceived;

        /// <summary>
        /// This method receives the requested market scanner data results
        /// </summary>
        /// <param name="reqId">The request's identifier</param>
        /// <param name="rank">The ranking within the response of this bar</param>
        /// <param name="contractDetails">This structure contains a full description of the contract that was executed</param>
        /// <param name="distance">Varies based on query</param>
        /// <param name="benchmark">Varies based on query</param>
        /// <param name="projection">Varies based on query</param>
        /// <param name="legsStr">Describes combo legs when scan is returning EFP</param>
        public void scannerData(int reqId, int rank, IBApi.ContractDetails contractDetails, string distance, string benchmark, string projection, string legsStr)
        {
            ScannerDataReceived?.Invoke(reqId, rank, contractDetails.ToContractDetails(), distance, benchmark, projection, legsStr);
        }

        /// <summary>
        /// Marks the end of one scan (the receipt of scanner data has ended)
        /// </summary>
        /// <param name="reqId">The request's identifier</param>
        public void scannerDataEnd(int reqId)
        {
            ScannerRequestEnded?.Invoke(reqId);
        }

        /// <summary>
        /// This method receives an XML document that describes the valid parameters that a scanner subscription can have
        /// </summary>
        /// <param name="xml">The xml-formatted string with the available parameters</param>
        public void scannerParameters(string xml)
        {
            ScannerParametersReceived?.Invoke(xml);
        }
    }
}
