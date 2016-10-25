using Capital.GSG.FX.Data.Core.ContractData;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI.Extensions
{
    public static class IBContractDetailsExtensions
    {
        private static void EnrichContractDetails(ref ContractDetails contractDetails, IBApi.ContractDetails ibContractDetails)
        {
            if (contractDetails == null)
                contractDetails = new ContractDetails();

            contractDetails.Summary = ibContractDetails.Summary.ToContract();
            contractDetails.MarketName = ibContractDetails.MarketName;
            contractDetails.MinimumPriceTick = ibContractDetails.MinTick;
            contractDetails.PriceMagnifier = ibContractDetails.PriceMagnifier;
            contractDetails.ValidOrderTypes = ibContractDetails.OrderTypes;
            contractDetails.ValidExchanges = ibContractDetails.ValidExchanges;
            contractDetails.UnderlyingContractID = ibContractDetails.UnderConId;
            contractDetails.LongName = ibContractDetails.LongName;
            contractDetails.ContractMonth = ibContractDetails.ContractMonth;
            contractDetails.Industry = ibContractDetails.Industry;
            contractDetails.Category = ibContractDetails.Category;
            contractDetails.SubCategory = ibContractDetails.Subcategory;
            contractDetails.TimeZoneCode = ibContractDetails.TimeZoneId;
            contractDetails.FullTradingHours = ParseTradingHours(ibContractDetails.TradingHours);
            contractDetails.RegularTradingHours = ParseTradingHours(ibContractDetails.LiquidHours);
            contractDetails.EconomicValueRule = ibContractDetails.EvRule;
            contractDetails.EconomicValueMultiplier = ibContractDetails.EvMultiplier;
            contractDetails.AllowedSecurityIdentifiers = ibContractDetails.SecIdList.ToSecIdentifierTypesList().ToList<SecIdentifierType>();
        }

        /// <summary>
        /// Parse trading hours string
        /// </summary>
        /// <param name="tradingHoursStr">20090507:0930-1600;20090508:CLOSED</param>
        /// <returns></returns>
        private static Dictionary<DateTime, TradingHours> ParseTradingHours(string tradingHoursStr)
        {
            string[] parts = tradingHoursStr.Split(';');

            if ((parts == null) || (parts.Length == 0))
                return null;

            Dictionary<DateTime, TradingHours> tradingHours = new Dictionary<DateTime, TradingHours>();

            foreach (string part in parts)
            {
                string[] subparts = part.Split(':');

                if ((subparts == null) || (subparts.Length != 2))
                    continue;

                DateTime date = DateTime.ParseExact(subparts[0], "yyyyMMdd", CultureInfo.InvariantCulture);

                if (subparts[1] == "CLOSED")
                {
                    tradingHours.Add(date, new TradingHours() { IsClosed = true });
                }
                else
                {
                    string[] timeParts = subparts[1].Split('-');

                    if ((timeParts == null) || (timeParts.Length != 2))
                        continue;

                    TimeSpan startTime = DateTime.ParseExact(timeParts[0], "HHmm", CultureInfo.InvariantCulture).TimeOfDay;
                    TimeSpan endTime = DateTime.ParseExact(timeParts[1], "HHmm", CultureInfo.InvariantCulture).TimeOfDay;

                    tradingHours.Add(date, new TradingHours() { StartTime = startTime, EndTime = endTime });
                }
            }

            return tradingHours;
        }

        public static ContractDetails ToContractDetails(this IBApi.ContractDetails ibContractDetails)
        {
            if (ibContractDetails == null)
                return null;
            else
            {
                ContractDetails contractDetails;

                // Special case for bonds
                if (!String.IsNullOrEmpty(ibContractDetails.Cusip)) // Bond
                {
                    contractDetails = new BondContractDetails()
                    {
                        Cusip = ibContractDetails.Cusip,
                        Ratings = ibContractDetails.Ratings,
                        DescriptionAppendix = ibContractDetails.DescAppend,
                        Type = ibContractDetails.BondType,
                        CouponType = ibContractDetails.CouponType,
                        IsCallable = ibContractDetails.Callable,
                        IsPutable = ibContractDetails.Putable,
                        Coupon = ibContractDetails.Coupon,
                        IsConvertible = ibContractDetails.Convertible,
                        Maturity = ibContractDetails.Maturity,
                        IssueDate = DateTime.Parse(ibContractDetails.IssueDate),
                        NextOptionDate = DateTime.Parse(ibContractDetails.NextOptionDate),
                        NextOptionType = ibContractDetails.NextOptionType,
                        IsNextOptionPartial = ibContractDetails.NextOptionPartial,
                        Notes = ibContractDetails.Notes
                    };
                }
                else // Other type of contract
                {
                    contractDetails = new ContractDetails();
                }

                EnrichContractDetails(ref contractDetails, ibContractDetails);

                return contractDetails;
            }
        }

        public static IEnumerable<ContractDetails> ToContractsDetails(this IEnumerable<IBApi.ContractDetails> ibContractsDetails)
        {
            return ibContractsDetails?.Select(ibContractDetails => ibContractDetails.ToContractDetails());
        }
    }
}
