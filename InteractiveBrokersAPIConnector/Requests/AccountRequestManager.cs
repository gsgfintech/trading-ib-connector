using IBApi;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI.Requests
{
    public class AccountRequestManager
    {
        private EClientSocket ClientSocket { get; set; }

        public AccountRequestManager(IBClientRequestsManager requestsManager)
        {
            ClientSocket = requestsManager.ClientSocket;
        }

        /// <summary>
        /// Subscribes to a specific account's information and portfolio. Use this method to start and stop a subscription to a single account. As a result of this subscription, 
        /// the account's information, portfolio and last update time will be received via the AccountUpdateTimeReceived, AccountValueUpdated and PortfolioUpdated events of the responses manager
        /// You can subscribe to only one account at a time. A second subscription request for another account when the previous subscription is still active will cause the first 
        /// one to be canceled in favor of the second. Consider using RequestPositions if you want to retrieve all your accounts' portfolios directly
        /// </summary>
        /// <param name="accountCode">The account code for which to receive account and portfolio updates</param>
        public void SubscribeToAccountUpdates(string accountCode)
        {
            // If set to TRUE, the client will start receiving account and portfolio updates. If set to FALSE, the client will stop receiving this information
            ClientSocket.reqAccountUpdates(true, accountCode);
        }

        /// <summary>
        /// Subscribes to a specific account's information and portfolio. Use this method to start and stop a subscription to a single account. As a result of this subscription, 
        /// the account's information, portfolio and last update time will be received via the AccountUpdateTimeReceived, AccountValueUpdated and PortfolioUpdated events of the responses manager
        /// You can subscribe to only one account at a time. A second subscription request for another account when the previous subscription is still active will cause the first 
        /// one to be canceled in favor of the second. Consider using RequestPositions if you want to retrieve all your accounts' portfolios directly
        /// </summary>
        /// <param name="accountCode">The account code for which to receive account and portfolio updates</param>
        public void UnsubscribeFromAccountUpdates(string accountCode)
        {
            // If set to TRUE, the client will start receiving account and portfolio updates. If set to FALSE, the client will stop receiving this information
            ClientSocket.reqAccountUpdates(false, accountCode);
        }

        /// <summary>
        /// This method will subscribe to the account summary as presented on the TWS Account Summary tab. The data is returned by AccountSummaryReceived
        /// Note: This request can only be made when connected to a Financial Advisor (FA) account
        /// </summary>
        /// <param name="requestID">The ID of the data request. Ensures that responses are matched to requests if several requests are in process</param>
        /// <param name="group">Set to All to return account summary data for all accounts, or set to a specific Advisor Account Group name that has already been created in TWS Global Configuration</param>
        /// <param name="accountTags">An enumerable of of account tags</param>
        public void RequestAccountSummary(int requestID, IEnumerable<string> accountTags, string group = "All")
        {
            string accountTagsStr = String.Empty;

            accountTagsStr = accountTags?.Aggregate<string, string>(String.Empty, (cur, next) => { return $"{cur},{next}"; });

            ClientSocket.reqAccountSummary(requestID, group, accountTagsStr);
        }

        /// <summary>
        /// Cancels the request for Account Window Summary tab data
        /// </summary>
        /// <param name="requestID">The ID of the data request being cancelled</param>
        public void CancelAccountSummaryRequest(int requestID)
        {
            ClientSocket.cancelAccountSummary(requestID);
        }

        /// <summary>
        /// Requests real-time position data for all accounts
        /// Note: This request can only be made when connected to a Financial Advisor (FA) account
        /// </summary>
        public void RequestAllPositions()
        {
            ClientSocket.reqPositions();
        }

        /// <summary>
        /// Cancels real-time position updates
        /// </summary>
        public void CancelRealTimePositionUpdates()
        {
            ClientSocket.cancelPositions();
        }
    }
}
