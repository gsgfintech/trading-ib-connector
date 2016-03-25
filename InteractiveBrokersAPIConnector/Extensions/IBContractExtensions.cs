using Net.Teirlinck.FX.Data.ContractData;
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
                    ComboLegs = contract.ComboLegs?.ToIBApiComboLegsList().ToList<IBApi.ComboLeg>(),
                    ComboLegsDescription = contract.ComboLegDescription,
                    ConId = contract.ContractID,
                    Currency = contract.Currency.ToString(),
                    Exchange = contract.Exchange,
                    Expiry = contract.Expiry,
                    IncludeExpired = contract.IncludeExpired.HasValue ? contract.IncludeExpired.Value : false,
                    LocalSymbol = contract.LocalSymbol,
                    Multiplier = contract.Multiplier,
                    PrimaryExch = contract.PrimaryExchange,
                    SecId = contract.SecIdentifier,
                    Strike = contract.Strike.HasValue ? contract.Strike.Value : 0,
                    Symbol = contract.Symbol.ToString(),
                    TradingClass = contract.TradingClass,
                    UnderComp = contract.UnderComp.ToIBApiUnderComp()
                };

                if (contract.SecIdentifierType != null)
                    ibContract.SecIdType = contract.SecIdentifierType.Code;

                if (contract.SecurityType != null)
                    ibContract.SecType = contract.SecurityType;

                if (contract.Right != null)
                    ibContract.Right = contract.Right.Code;

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
            else
                return new Contract()
                {
                    ContractID = ibContract.ConId,
                    Symbol = CurrencyUtils.GetFromStr(ibContract.Symbol),
                    SecurityType = ibContract.SecType,
                    Expiry = ibContract.Expiry,
                    Strike = ibContract.Strike,
                    Right = OptionSides.GetFromString(ibContract.Right),
                    Multiplier = ibContract.Multiplier,
                    ComboLegs = ibContract.ComboLegs.ToComboLegsList(),
                    Exchange = ibContract.Exchange,
                    Currency = CurrencyUtils.GetFromStr(ibContract.Currency),
                    LocalSymbol = ibContract.LocalSymbol,
                    PrimaryExchange = ibContract.PrimaryExch,
                    TradingClass = ibContract.TradingClass,
                    IncludeExpired = ibContract.IncludeExpired,
                    SecIdentifierType = SecIdentifierTypes.GetFromString(ibContract.SecIdType),
                    SecIdentifier = ibContract.SecId,
                    UnderComp = ibContract.UnderComp.ToUnderComp()
                };
        }

        public static IEnumerable<Contract> ToContracts(this IEnumerable<IBApi.Contract> ibContracts)
        {
            return ibContracts?.Select(ibContract => ibContract.ToContract());
        }
    }
}
