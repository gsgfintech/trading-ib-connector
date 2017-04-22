using Capital.GSG.FX.Data.Core.FinancialAdvisorsData;
using System;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI
{
    public partial class IBClientResponsesManager
    {
        public event Action<string> ManagedAccountsListReceived;
        public event Action<FinancialAdvisorsDataType, string> FinancialAdvisorsDataReceived;

        /// <summary>
        /// Receives a comma-separated string containing IDs of managed accounts
        /// </summary>
        /// <param name="accountsList">The comma delimited list of FA managed accounts</param>
        public void managedAccounts(string accountsList)
        {
            ManagedAccountsListReceived?.Invoke(accountsList);
        }

        /// <summary>
        /// This method receives Financial Advisor configuration information from TWS
        /// </summary>
        /// <param name="faDataType">Specifies the type of Financial Advisor configuration data being received from TWS. Valid values include:
        /// 1 = GROUPS
        /// 2 = PROFILE
        /// 3 =ACCOUNT ALIASES</param>
        /// <param name="faXmlData">The XML string containing the previously requested FA configuration information</param>
        public void receiveFA(int faDataType, string faXmlData)
        {
            FinancialAdvisorsDataReceived?.Invoke((FinancialAdvisorsDataType)faDataType, faXmlData);
        }
    }
}
