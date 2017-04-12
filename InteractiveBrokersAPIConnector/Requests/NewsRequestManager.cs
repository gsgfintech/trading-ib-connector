using Capital.GSG.FX.Data.Core.ContractData;
using Net.Teirlinck.FX.InteractiveBrokersAPI.Extensions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI.Requests
{
    public class NewsRequestManager
    {
        private IBApi.EClientSocket ClientSocket { get; set; }

        public NewsRequestManager(IBClientRequestsManager requestsManager)
        {
            ClientSocket = requestsManager.ClientSocket;
        }

        /// <summary>
        /// Requests historical news headlines (replied with EWrapper::historicalNews, EWrapper::historicalNewsEnd)
        /// </summary>
        /// <param name="requestId">Request ID</param>
        /// <param name="contractId">Contract ID of ticker</param>
        /// <param name="providerCodes">a '+'-separated list of provider codes</param>
        /// <param name="startDateTime">Marks the (exclusive) start of the date range. The format is yyyy-MM-dd HH:mm:ss.0</param>
        /// <param name="endDateTime">Marks the (inclusive) end of the date range. The format is yyyy-MM-dd HH:mm:ss.0</param>
        /// <param name="maxResultsCount">The maximum number of headlines to fetch (1 - 300)</param>
        public void RequestHistoricalNews(int requestId, int contractId, string providerCodes, DateTimeOffset startDateTime, DateTimeOffset endDateTime, int maxResultsCount)
        {
            ClientSocket.reqHistoricalNews(requestId, contractId, providerCodes, startDateTime.ToString("yyyy-MM-dd HH:mm:ss.0"), endDateTime.ToString("yyyy-MM-dd HH:mm:ss.0"), maxResultsCount);
        }

        /// <summary>
        /// Requests news article body given article ID (replied with EWrapper::newsArticle)
        /// </summary>
        /// <param name="requestId"></param>
        /// <param name="providerCode"></param>
        /// <param name="articleId"></param>
        public void RequestNewsArticle(int requestId, string providerCode, string articleId)
        {
            ClientSocket.reqNewsArticle(requestId, providerCode, articleId);
        }

        /// <summary>
        /// Requests news providers which the user has subscribed to (replied with EWrapper::newsProviders)
        /// </summary>
        public void RequestNewsProviders()
        {
            ClientSocket.reqNewsProviders();
        }
    }
}
