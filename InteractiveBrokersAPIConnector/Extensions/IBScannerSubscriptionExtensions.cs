using Capital.GSG.FX.Data.Core.MarketScanner;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI.Extensions
{
    public static class IBScannerSubscriptionExtensions
    {
        public static IBApi.ScannerSubscription ToIBScannerSubscription(this ScannerSubscription subscription)
        {
            if (subscription == null)
                return null;
            else
                return new IBApi.ScannerSubscription()
                {
                    NumberOfRows = subscription.NumberOfRows,
                    Instrument = subscription.Instrument,
                    LocationCode = subscription.LocationCode,
                    ScanCode = subscription.ScanCode,
                    AbovePrice = (subscription.AbovePrice.HasValue) ? subscription.AbovePrice.Value : 0,
                    BelowPrice = (subscription.BelowPrice.HasValue) ? subscription.BelowPrice.Value : 0,
                    AboveVolume = (subscription.AboveVolume.HasValue) ? subscription.AboveVolume.Value : 0,
                    AverageOptionVolumeAbove = (subscription.AboveAverageOptionVolume.HasValue) ? subscription.AboveAverageOptionVolume.Value : 0,
                    MarketCapAbove = (subscription.MarketCapAbove.HasValue) ? subscription.MarketCapAbove.Value : 0,
                    MarketCapBelow = (subscription.MarketCapBelow.HasValue) ? subscription.MarketCapBelow.Value : 0,
                    MoodyRatingAbove = subscription.MoodyRatingAbove,
                    MoodyRatingBelow = subscription.MoodyRatingBelow,
                    SpRatingAbove = subscription.SPRatingAbove,
                    SpRatingBelow = subscription.SPRatingBelow,
                    MaturityDateAbove = (subscription.MaturityDateAbove.HasValue) ? subscription.MaturityDateAbove.Value.ToShortDateString() : String.Empty,
                    MaturityDateBelow = (subscription.MaturityDateBelow.HasValue) ? subscription.MaturityDateBelow.Value.ToShortDateString() : String.Empty,
                    CouponRateAbove = (subscription.CouponRateAbove.HasValue) ? subscription.CouponRateAbove.Value : 0,
                    CouponRateBelow = (subscription.CouponRateBelow.HasValue) ? subscription.CouponRateBelow.Value : 0,
                    ExcludeConvertible = subscription.ExcludeConvertible,
                    ScannerSettingPairs = subscription.ScannerSettingPairs,
                    StockTypeFilter = subscription.StockTypeFilter.Code
                };
        }

        public static ScannerSubscription ToScannerSubscription(this IBApi.ScannerSubscription ibSubscription)
        {
            if (ibSubscription == null)
                return null;
            else
                return new ScannerSubscription()
                {
                    NumberOfRows = ibSubscription.NumberOfRows,
                    Instrument = ibSubscription.Instrument,
                    LocationCode = ibSubscription.LocationCode,
                    ScanCode = ibSubscription.ScanCode,
                    AbovePrice = (ibSubscription.AbovePrice != 0.0) ? ibSubscription.AbovePrice : (double?)null,
                    BelowPrice = (ibSubscription.BelowPrice != 0.0) ? ibSubscription.BelowPrice : (double?)null,
                    AboveVolume = (ibSubscription.AboveVolume != 0) ? ibSubscription.AboveVolume : (int?)null,
                    AboveAverageOptionVolume = (ibSubscription.AverageOptionVolumeAbove != 0) ? ibSubscription.AverageOptionVolumeAbove : (int?)null,
                    MarketCapAbove = (ibSubscription.MarketCapAbove != 0.0) ? ibSubscription.MarketCapAbove : (double?)null,
                    MarketCapBelow = (ibSubscription.MarketCapBelow != 0.0) ? ibSubscription.MarketCapBelow : (double?)null,
                    MoodyRatingAbove = ibSubscription.MoodyRatingAbove,
                    MoodyRatingBelow = ibSubscription.MoodyRatingBelow,
                    SPRatingAbove = ibSubscription.SpRatingAbove,
                    SPRatingBelow = ibSubscription.SpRatingBelow,
                    MaturityDateAbove = (!String.IsNullOrEmpty(ibSubscription.MaturityDateAbove)) ? DateTime.Parse(ibSubscription.MaturityDateAbove) : (DateTime?)null,
                    MaturityDateBelow = (!String.IsNullOrEmpty(ibSubscription.MaturityDateBelow)) ? DateTime.Parse(ibSubscription.MaturityDateBelow) : (DateTime?)null,
                    CouponRateAbove = (ibSubscription.CouponRateAbove != 0.0) ? ibSubscription.CouponRateAbove : (double?)null,
                    CouponRateBelow = (ibSubscription.CouponRateBelow != 0.0) ? ibSubscription.CouponRateBelow : (double?)null,
                    ExcludeConvertible = ibSubscription.ExcludeConvertible,
                    ScannerSettingPairs = ibSubscription.ScannerSettingPairs,
                    StockTypeFilter = StockTypeUtils.GetFromStrCode(ibSubscription.StockTypeFilter)
                };
        }

        public static IEnumerable<IBApi.ScannerSubscription> ToIBScannerSubscriptionsList(this IEnumerable<ScannerSubscription> subscriptions)
        {
            return subscriptions?.Select(subscription => subscription.ToIBScannerSubscription());
        }

        public static IEnumerable<ScannerSubscription> ToScannerSubscriptionsList(this IEnumerable<IBApi.ScannerSubscription> ibSubscriptions)
        {
            return ibSubscriptions?.Select(ibSubcription => ibSubcription.ToScannerSubscription());
        }
    }
}
