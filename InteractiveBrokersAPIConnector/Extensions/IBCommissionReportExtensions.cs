using Capital.GSG.FX.Data.Core.ContractData;
using Capital.GSG.FX.Data.Core.ExecutionData;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI.Extensions
{
    public static class IBCommissionReportExtensions
    {
        public static CommissionReport ToCommissionReport(this IBApi.CommissionReport ibComReport)
        {
            if (ibComReport == null)
                return null;
            else
            {
                CommissionReport comReport = new CommissionReport()
                {
                    ExecutionID = ibComReport.ExecId.Replace('+', '-'),
                    Commission = ibComReport.Commission,
                    Currency = CurrencyUtils.GetFromStr(ibComReport.Currency),
                    RealizedPnL = ibComReport.RealizedPNL > -1e9 && ibComReport.RealizedPNL < 1e9 ? ibComReport.RealizedPNL : (double?)null, // sometimes we receive Double.Max or Double.Min from IB. No point to keep this
                    Yield = ibComReport.Yield > -1e9 && ibComReport.Yield < 1e9 ? ibComReport.Yield : (double?)null, // sometimes we receive Double.Max or Double.Min from IB. No point to keep this,
                    YieldRedemptionDateInt = ibComReport.YieldRedemptionDate
                };

                DateTime yieldRedemptionDate;
                if (DateTime.TryParse(ibComReport.YieldRedemptionDate.ToString(), out yieldRedemptionDate))
                    comReport.YieldRedemptionDate = yieldRedemptionDate;

                return comReport;
            }
        }

        public static IEnumerable<CommissionReport> ToCommissionReports(this IEnumerable<IBApi.CommissionReport> ibComReports)
        {
            return ibComReports?.Select(ibComReport => ibComReport.ToCommissionReport());
        }
    }
}
