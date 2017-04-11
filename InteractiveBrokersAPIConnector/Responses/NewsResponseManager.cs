using IBApi;
using System;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI
{
    public partial class IBClientResponsesManager
    {
        /// <summary>
        /// Returns data histogram. In response to EClient::reqHistogramData
        /// </summary>
        /// <param name="reqId"></param>
        /// <param name="data">Returned Tuple of histogram data, number of trades at specified price level</param>
        public void histogramData(int reqId, Tuple<double, long>[] data)
        {
            // TODO
        }

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
            // TODO
        }

        /// <summary>
        /// Ticks with news headline. In response to EClient::reqMktData
        /// </summary>
        /// <param name="tickerId"></param>
        /// <param name="timeStamp"></param>
        /// <param name="providerCode"></param>
        /// <param name="articleId"></param>
        /// <param name="headline"></param>
        /// <param name="extraData"></param>
        public void tickNews(int tickerId, long timeStamp, string providerCode, string articleId, string headline, string extraData)
        {
            // TODO
        }
    }
}
