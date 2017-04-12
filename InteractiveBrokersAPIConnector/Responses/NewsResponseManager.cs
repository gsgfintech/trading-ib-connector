using IBApi;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI
{
    public partial class IBClientResponsesManager
    {
        public event Action<Dictionary<string, string>> NewsProvidersListReceived;

        /// <summary>
        /// Returns news headline. In response to EClient::reqHistoricalNews
        /// </summary>
        /// <param name="requestId"></param>
        /// <param name="time"></param>
        /// <param name="providerCode"></param>
        /// <param name="articleId"></param>
        /// <param name="headline"></param>
        public void historicalNews(int requestId, string time, string providerCode, string articleId, string headline)
        {
            // TODO
        }

        /// <summary>
        /// Returns news headlines end marker. In response to EClient::reqHistoricalNews
        /// </summary>
        /// <param name="requestId"></param>
        /// <param name="hasMore">Indicates whether there are more results available, false otherwise</param>
        public void historicalNewsEnd(int requestId, bool hasMore)
        {
            // TODO
        }

        /// <summary>
        /// Called when receives News Article. In response to EClient::reqNewsArticle
        /// </summary>
        /// <param name="requestId">The request ID used in the call to EClient::reqNewsArticle</param>
        /// <param name="articleType">The type of news article (0 - plain text or html, 1 - binary data / pdf)</param>
        /// <param name="articleText">The body of article (if articleType == 1, the binary data is encoded using the Base64 scheme)</param>
        public void newsArticle(int requestId, int articleType, string articleText)
        {
            // TODO
        }

        /// <summary>
        /// Returns array of news providers. In response to EClient::reqNewsProviders
        /// </summary>
        /// <param name="newsProviders"></param>
        public void newsProviders(NewsProvider[] newsProviders)
        {
            if (newsProviders.Length == 0)
                NewsProvidersListReceived?.Invoke(new Dictionary<string, string>());
            else
                NewsProvidersListReceived?.Invoke(newsProviders.ToDictionary(p => p.ProviderName, p => p.ProviderCode));
        }
    }
}
