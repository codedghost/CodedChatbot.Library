using System;
using System.Linq;
using CoreCodedChatbot.Config;
using CoreCodedChatbot.Database.Context.Interfaces;
using CoreCodedChatbot.Database.Context.Models;
using CoreCodedChatbot.Library.Interfaces.Services;
using CoreCodedChatbot.Library.Models.Data;
using Microsoft.Extensions.Logging;

namespace CoreCodedChatbot.Library.Services
{
    public class VipService : IVipService
    {
        private IChatbotContextFactory _chatbotContextFactory;
        private readonly IConfigService _configService;
        private readonly ILogger<IVipService> _logger;

        public VipService(
            IChatbotContextFactory chatbotContextFactory, 
            IConfigService configService,
            ILogger<IVipService> logger)
        {
            _chatbotContextFactory = chatbotContextFactory;
            _configService = configService;
            _logger = logger;
        }

        public bool GiftVip(string donorUsername, string receiverUsername)
        {
            var donorUser = GetUser(donorUsername);
            var receiverUser = GetUser(receiverUsername);

            if (donorUser == null || receiverUser == null) return false;

            return GiftVip(donorUser, receiverUser);
        }

        public bool RefundVip(string username, bool deferSave = false)
        {
            try
            {
                using (var context = _chatbotContextFactory.Create())
                {
                    var user = context.Users.SingleOrDefault(u => u.Username == username);

                    if (user == null) return false;

                    user.ModGivenVipRequests++;

                    if (!deferSave) context.SaveChanges();
                }

                return true;
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Error when refunding Vip token. username: {username}, defersave: {deferSave}");
                return false;
            }
        }

        public bool RefundSuperVip(string username, bool deferSave = false)
        {
            try
            {
                using (var context = _chatbotContextFactory.Create())
                {
                    var user = context.Users.SingleOrDefault(u => u.Username == username);

                    if (user == null) return false;

                    user.ModGivenVipRequests += _configService.Get<int>("SuperVipCost");

                    if (!deferSave) context.SaveChanges();
                }

                return true;
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Error when refunding Super Vip. username: {username}, deferSave: {deferSave}");
                return false;
            }
        }

        public bool HasVip(string username)
        {
            try
            {
                var user = GetUser(username);

                return user != null && new VipRequests(_configService, user).TotalRemaining > 0;
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Error when checking if User has a Vip token. username: {username}");
                return false;
            }
        }

        public bool UseVip(string username)
        {
            try
            {
                if (!HasVip(username)) return false;

                using (var context = _chatbotContextFactory.Create())
                {
                    var user = context.Users.Find(username);

                    user.UsedVipRequests++;
                    context.SaveChanges();
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Error when attempting to deduct a user's Vip token. username: {username}");
                return false;
            }

            return true;
        }

        public bool HasSuperVip(string username)
        {
            try
            {
                var user = GetUser(username);

                return user != null && new VipRequests(_configService, user).TotalRemaining > _configService.Get<int>("SuperVipCost");
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Error when checking if a user has enough for a Super Vip token. username: {username}");
                return false;
            }
        }

        public bool UseSuperVip(string username)
        {
            try
            {
                if (!HasSuperVip(username)) return false;

                using (var context = _chatbotContextFactory.Create())
                {
                    var user = context.Users.Find(username);

                    user.UsedSuperVipRequests++;
                    context.SaveChanges();
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Error when attempting to deduct a user's Super Vip token. username: {username}");
                return false;
            }

            return true;
        }

        public bool ModGiveVip(string username, int numberOfVips)
        {
            try
            {
                // TEMP PATCH
                GetUser(username);

                using (var context = _chatbotContextFactory.Create())
                {
                    // User is guaranteed to now be available in the context

                    var user = context.Users.Find(username);

                    user.ModGivenVipRequests += numberOfVips;
                    context.SaveChanges();
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Error when a mod has attempted to give 1 or more Vips to a user. username: {username}, numberOfVips: {numberOfVips}");
                return false;
            }

            return true;
        }

        private bool GiftVip(User donor, User receiver)
        {
            try
            {
                if (!HasVip(donor.Username)) return false;

                using (var context = _chatbotContextFactory.Create())
                {
                    var donorUser = context.Users.Find(donor.Username);
                    var receiverUser = context.Users.Find(receiver.Username);



                    donorUser.SentGiftVipRequests++;
                    receiverUser.ReceivedGiftVipRequests++;

                    context.SaveChanges();
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Error when Gifting a Vip. DonorUsername: {donor.Username}, ReceiverUsername: {receiver.Username}");
                return false;
            }

            return true;
        }

        private User GetUser(string username, bool createUser = true)
        {
            using (var context = _chatbotContextFactory.Create())
            {
                var user = context.Users.Find(username.ToLower());

                if (user == null && createUser)
                    user = this.AddUser(context, username, false);

                return user;
            }
        }

        private User AddUser(IChatbotContext context, string username, bool deferSave)
        {
            var userModel = new User
            {
                Username = username.ToLower(),
                UsedVipRequests = 0,
                ModGivenVipRequests = 0,
                FollowVipRequest = 0,
                SubVipRequests = 0,
                DonationOrBitsVipRequests = 0,
                TokenBytes = 0,
                ReceivedGiftVipRequests = 0,
                SentGiftVipRequests = 0
            };

            try
            {
                context.Users.Add(userModel);
                if (!deferSave) context.SaveChanges();
            }
            catch (Exception)
            {
                return null;
            }

            return userModel;
        }
    }
}
