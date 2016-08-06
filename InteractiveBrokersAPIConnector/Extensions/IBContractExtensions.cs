using Net.Teirlinck.FX.Data.AccountPortfolio;
using Net.Teirlinck.FX.Data.ContractData;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI.Extensions
{
    public static class IBContractExtensions
    {
        public static IBApi.Contract ToIBContract(this Contract contract)
        {
            if (contract == null)
                return null;
            else
            {
                IBApi.Contract ibContract = new IBApi.Contract()
                {
                    ConId = contract.ContractID,
                    Currency = contract.Currency.ToString(),
                    Exchange = contract.Exchange,
                    Expiry = contract.Expiry,
                    IncludeExpired = contract.IncludeExpired.HasValue ? contract.IncludeExpired.Value : false,
                    LocalSymbol = contract.LocalSymbol,
                    Multiplier = contract.Multiplier,
                    PrimaryExch = contract.PrimaryExchange,
                    SecType = contract.SecurityType.ToString(),
                    SecId = contract.SecIdentifier,
                    Strike = contract.Strike.HasValue ? contract.Strike.Value : 0,
                    Symbol = contract.Symbol.ToString(),
                    TradingClass = contract.TradingClass,
                };

                if (contract.SecIdentifierType.HasValue)
                    ibContract.SecIdType = contract.SecIdentifierType.ToString();

                return ibContract;
            }
        }

        public static IEnumerable<IBApi.Contract> ToIBContracts(this IEnumerable<Contract> contracts)
        {
            return contracts?.Select(contract => contract.ToIBContract());
        }

        public static Contract ToContract(this IBApi.Contract ibContract)
        {
            if (ibContract == null)
                return null;

            Contract contract = new Contract()
            {
                Broker = Broker.IB,
                ContractID = ibContract.ConId,
                Symbol = CurrencyUtils.GetFromStr(ibContract.Symbol),
                SecurityType = (SecurityType)Enum.Parse(typeof(SecurityType), ibContract.SecType, true),
                Expiry = ibContract.Expiry,
                Strike = ibContract.Strike,
                Multiplier = ibContract.Multiplier,
                Exchange = ibContract.Exchange,
                Currency = CurrencyUtils.GetFromStr(ibContract.Currency),
                LocalSymbol = ibContract.LocalSymbol,
                PrimaryExchange = ibContract.PrimaryExch,
                TradingClass = ibContract.TradingClass,
                IncludeExpired = ibContract.IncludeExpired,
                SecIdentifier = ibContract.SecId
            };

            SecIdentifierType secIdType;
            if (SecIdentifierTypeUtils.TryParse(ibContract.SecIdType, out secIdType))
                contract.SecIdentifierType = secIdType;

            return contract;
        }

        public static IEnumerable<Contract> ToContracts(this IEnumerable<IBApi.Contract> ibContracts)
        {
            return ibContracts?.Select(ibContract => ibContract.ToContract());
        }
    }
}
