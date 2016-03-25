using Net.Teirlinck.FX.Data.ContractData;
using System.Collections.Generic;
using System.Linq;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI.Extensions
{
    public static class IBComboLegExtensions
    {
        public static IBApi.ComboLeg ToIBApiComboLeg(this ComboLeg comboLeg)
        {
            if (comboLeg == null)
                return null;
            else
                return new IBApi.ComboLeg()
                {
                    Action = comboLeg.Action,
                    ConId = comboLeg.ConId,
                    DesignatedLocation = comboLeg.DesignatedLocation,
                    Exchange = comboLeg.Exchange,
                    ExemptCode = comboLeg.OpenClose,
                    Ratio = comboLeg.Ratio,
                    ShortSaleSlot = comboLeg.ShortSaleSlot
                };
        }

        public static IEnumerable<IBApi.ComboLeg> ToIBApiComboLegsList(this IEnumerable<ComboLeg> comboLegs)
        {
            return comboLegs?.Select(comboLeg => comboLeg.ToIBApiComboLeg());
        }

        public static ComboLeg ToComboLeg(this IBApi.ComboLeg ibComboLeg)
        {
            if (ibComboLeg == null)
                return null;
            else
                return new ComboLeg()
                {
                    Action = ibComboLeg.Action,
                    ConId = ibComboLeg.ConId,
                    DesignatedLocation = ibComboLeg.DesignatedLocation,
                    Exchange = ibComboLeg.Exchange,
                    ExemptCode = ibComboLeg.OpenClose,
                    Ratio = ibComboLeg.Ratio,
                    ShortSaleSlot = ibComboLeg.ShortSaleSlot
                };
        }

        public static IEnumerable<ComboLeg> ToComboLegsList(this IEnumerable<IBApi.ComboLeg> ibComboLegs)
        {
            return ibComboLegs?.Select(ibComboLeg => ibComboLeg.ToComboLeg());
        }
    }
}
