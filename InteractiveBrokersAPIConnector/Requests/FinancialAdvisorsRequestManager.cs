using Capital.GSG.FX.Data.Core.FinancialAdvisorsData;
using IBApi;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI.Requests
{
    public class FinancialAdvisorsRequestManager
    {
        private EClientSocket ClientSocket { get; set; }

        public FinancialAdvisorsRequestManager(IBClientRequestsManager requestsManager)
        {
            ClientSocket = requestsManager.ClientSocket;
        }

        /// <summary>
        /// Requests the accounts to which the logged-in user has access. The list will be returned by ManagedAccountsListReceived event of responses manager
        /// This request can only be made when connected to a Financial Advisor (FA) account
        /// </summary>
        public void RequestManagedAccounts()
        {
            ClientSocket.reqManagedAccts();
        }

        /// <summary>
        /// Requests Financial Advisor configuration information from TWS. The data returns in an XML string via the FinancialAdvisorsDataReceived event of the responses manager
        /// </summary>
        /// <param name="dataType">Specifies the type of Financial Advisor configuration data being requested</param>
        public void RequestFinancialAdvisorConfiguration(FinancialAdvisorsDataType dataType)
        {
            ClientSocket.requestFA((int)dataType);
        }

        /// <summary>
        /// Call this method to request new FA configuration information from TWS. The data returns in an XML string via the FinancialAdvisorsDataReceived event of the responses manager
        /// </summary>
        /// <param name="dataType">Specifies the type of Financial Advisor configuration data being requested</param>
        /// <param name="newXmlConfiguration">The XML string containing the new FA configuration information</param>
        public void ReplaceFinancialAdvisorConfiguration(FinancialAdvisorsDataType dataType, string newXmlConfiguration)
        {
            ClientSocket.replaceFA((int)dataType, newXmlConfiguration);
        }
    }
}
