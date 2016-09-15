using Net.Teirlinck.FX.Data.ContractData;
using Net.Teirlinck.FX.InteractiveBrokersAPI.Extensions;
using System;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI
{
    public partial class IBClientResponsesManager
    {
        public event Action<string> AccountDownloaded;
        public event Action<int, string, string, string, string> AccountSummaryReceived;
        public event Action<int> AccountSummaryEnded;
        public event Action<DateTimeOffset> AccountUpdateTimeReceived;
        public event Action<string, string, Currency, string> AccountValueUpdated;
        public event Action<Contract, int, double, double, double, double, double, string> PortfolioUpdated;
        public event Action<string, Contract, double, double> PositionReceived;
        public event Action PositionRequestEnded;

        /// <summary>
        /// This event is called when the receipt of an account's information has been completed
        /// </summary>
        /// <param name="account"></param>
        public void accountDownloadEnd(string account)
        {
            AccountDownloaded?.Invoke(account);
        }

        /// <summary>
        /// Returns the account information from TWS in response to reqAccountSummary()
        /// </summary>
        /// <param name="reqId">The request's unique identifier</param>
        /// <param name="account">The account ID</param>
        /// <param name="tag">The account attribute being received</param>
        /// <param name="value">The value of the attribute</param>
        /// <param name="currency">The currency in which the attribute is expressed</param>
        public void accountSummary(int reqId, string account, string tag, string value, string currency)
        {
            AccountSummaryReceived?.Invoke(reqId, account, tag, value, currency);
        }

        /// <summary>
        /// This is called once all account information for a given reqAccountSummary() request are received
        /// </summary>
        /// <param name="reqId">The request's identifier</param>
        public void accountSummaryEnd(int reqId)
        {
            AccountSummaryEnded?.Invoke(reqId);
        }

        /// <summary>
        /// Receives the last time at which the account was updated
        /// </summary>
        /// <param name="timestamp">The last update system time</param>
        public void updateAccountTime(string timestamp)
        {
            AccountUpdateTimeReceived?.Invoke(DateTimeOffset.Parse(timestamp));
        }

        /// <summary>
        /// This callback receives the subscribed account's information in response to reqAccountUpdates(). You can only subscribe to one account at a time
        /// </summary>
        /// <param name="key">A string that indicates one type of account value (one of class Account member values</param>
        /// <param name="value">The value associated with the key</param>
        /// <param name="currency">Defines the currency type, in case the value is a currency type</param>
        /// <param name="accountName">The account. Useful for Financial Advisor sub-account messages</param>
        public void updateAccountValue(string key, string value, string currency, string accountName)
        {
            AccountValueUpdated?.Invoke(key, value, CurrencyUtils.GetFromStr(currency), accountName);
        }

        /// <summary>
        /// Receives the subscribed account's portfolio in response to reqAccountUpdates(). If you want to receive the portfolios of all managed accounts, use reqPositions().
        /// </summary>
        /// <param name="contract">This structure contains a description of the contract which is being traded. The exchange field in a contract is not set for portfolio update</param>
        /// <param name="position">The number of positions held. If the position is 0, it means the position has just cleared</param>
        /// <param name="marketPrice">The unit price of the instrument</param>
        /// <param name="marketValue">The total market value of the instrument</param>
        /// <param name="averageCost">The average cost per share is calculated by dividing your cost (execution price + commission) by the quantity of your position</param>
        /// <param name="unrealisedPNL">The difference between the current market value of your open positions and the average cost, or Value - Average Cost</param>
        /// <param name="realisedPNL">Shows your profit on closed positions, which is the difference between your entry execution cost (execution price + commissions to open the position) and exit execution cost ((execution price + commissions to close the position)</param>
        /// <param name="accountName">The name of the account to which the message applies.  Useful for Financial Advisor sub-account messages</param>
        public void updatePortfolio(IBApi.Contract contract, int position, double marketPrice, double marketValue, double averageCost, double unrealisedPNL, double realisedPNL, string accountName)
        {
            PortfolioUpdated?.Invoke(contract.ToContract(), position, marketPrice, marketValue, averageCost, unrealisedPNL, realisedPNL, accountName);
        }

        /// <summary>
        /// This event returns open positions for all accounts in response to the reqPositions() method
        /// </summary>
        /// <param name="account">The account holding the positions</param>
        /// <param name="contract">This structure contains a full description of the position's contract</param>
        /// <param name="pos">The number of positions held</param>
        /// <param name="avgCost">The average cost of the position</param>
        public void position(string account, IBApi.Contract contract, int pos, double avgCost)
        {
            PositionReceived?.Invoke(account, contract.ToContract(), (double)pos, avgCost);
        }

        /// <summary>
        /// This is called once all position data for a given request are received and functions as an end marker for the position() data
        /// </summary>
        public void positionEnd()
        {
            PositionRequestEnded?.Invoke();
        }
    }
}
