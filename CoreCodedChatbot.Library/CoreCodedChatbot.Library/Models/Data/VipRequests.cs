using CoreCodedChatbot.Database.Context.Models;
using CoreCodedChatbot.Library.Helpers;
using CoreCodedChatbot.Library.Interfaces.Services;

namespace CoreCodedChatbot.Library.Models.Data
{
    public class VipRequests
    {
        private int _superVipCost;

        public VipRequests(IConfigService configService, User user)
        {
            _superVipCost = configService.Get<int>("SuperVipCost");

            Donations = user.DonationOrBitsVipRequests;
            Follow = user.FollowVipRequest;
            ModGiven = user.ModGivenVipRequests;
            Sub = user.SubVipRequests;
            Byte = user.TokenVipRequests;
            ReceivedGift = user.ReceivedGiftVipRequests;
            Used = user.UsedVipRequests;
            SentGift = user.SentGiftVipRequests;
            UsedSuperVipRequests = user.UsedSuperVipRequests;
        }

        public int Donations { get; set; }
        public int Follow { get; set; }
        public int ModGiven { get; set; }
        public int Sub { get; set; }
        public int Byte { get; set; }
        public int Used { get; set; }
        public int SentGift { get; set; }
        public int ReceivedGift { get; set; }
        public int UsedSuperVipRequests { get; set; }

        public int TotalRemaining => (Donations + Follow + ModGiven + Sub + Byte + ReceivedGift) - (UsedSuperVipRequests * _superVipCost) - Used - SentGift;
    }
}
