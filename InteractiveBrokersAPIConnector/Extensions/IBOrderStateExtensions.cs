using Net.Teirlinck.FX.Data.OrderData;
using System.Collections.Generic;
using System.Linq;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI.Extensions
{
    public static class IBOrderStateExtensions
    {
        public static OrderState ToOrderStatus(this IBApi.OrderState ibStatus)
        {
            if (ibStatus == null)
                return null;
            else
                return new OrderState()
                {
                    Status = ibStatus.Status,
                    InitialMarginImpact = ibStatus.InitMargin,
                    MaintenanceMarginImpact = ibStatus.MaintMargin,
                    EquityWithLoanValueImpact = ibStatus.EquityWithLoan,
                    Commission = (ibStatus.Commission > -1e9 && ibStatus.Commission < 1e9) ? ibStatus.Commission : (double?)null,
                    MinCommission = (ibStatus.MinCommission > -1e9 && ibStatus.MinCommission < 1e9) ? ibStatus.MinCommission : (double?)null,
                    MaxCommission = (ibStatus.MaxCommission > -1e9 && ibStatus.MaxCommission < 1e9) ? ibStatus.MaxCommission : (double?)null,
                    CommissionCurrency = ibStatus.CommissionCurrency,
                    WarningMessage = ibStatus.WarningText
                };
        }

        public static IBApi.OrderState ToIBOrderStatus(this OrderState status)
        {
            if (status == null)
                return null;
            else
                return new IBApi.OrderState()
                {
                    Status = status.Status,
                    InitMargin = status.InitialMarginImpact,
                    MaintMargin = status.MaintenanceMarginImpact,
                    EquityWithLoan = status.EquityWithLoanValueImpact,
                    Commission = status.Commission ?? 0.0,
                    MaxCommission = status.MaxCommission ?? 0.0,
                    MinCommission = status.MinCommission ?? 0.0,
                    CommissionCurrency = status.CommissionCurrency,
                    WarningText = status.WarningMessage
                };
        }

        public static IEnumerable<OrderState> ToOrderStatusList(this IEnumerable<IBApi.OrderState> ibStatuses)
        {
            return ibStatuses?.Select(ibStatus => ibStatus.ToOrderStatus());
        }

        public static IEnumerable<IBApi.OrderState> ToIBOrderStatusList(this IEnumerable<OrderState> statuses)
        {
            return statuses?.Select(status => status.ToIBOrderStatus());
        }
    }
}
