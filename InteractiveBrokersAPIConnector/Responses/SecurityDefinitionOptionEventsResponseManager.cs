using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI
{
    public partial class IBClientResponsesManager
    {
        /// <summary>
        /// Returns the option chain for an underlying on an exchange specified in reqSecDefOptParams
		/// There will be multiple callbacks to securityDefinitionOptionParameter if multiple exchanges are specified in reqSecDefOptParams
        /// </summary>
        /// <param name="reqId">ID of the request initiating the callback</param>
        /// <param name="exchange"></param>
        /// <param name="underlyingConId">The conID of the underlying security</param>
        /// <param name="tradingClass">the option trading class</param>
        /// <param name="multiplier">the option multiplier</param>
        /// <param name="expirations">a list of the expiries for the options of this underlying on this exchange</param>
        /// <param name="strikes">a list of the possible strikes for options of this underlying on this exchange</param>
        public void securityDefinitionOptionParameter(int reqId, string exchange, int underlyingConId, string tradingClass, string multiplier, HashSet<string> expirations, HashSet<double> strikes)
        {
            // Not sure what this is supposed to do...
        }

        /// <summary>
        /// called when all callbacks to securityDefinitionOptionParameter are complete
        /// </summary>
        /// <param name="reqId">the ID used in the call to securityDefinitionOptionParameter</param>
        public void securityDefinitionOptionParameterEnd(int reqId)
        {
            // Not sure what this is supposed to do...
        }
    }
}
