using System;
using System.Linq;
using CoreCodedChatbot.ApiContract.RequestModels.StreamStatus;
using CoreCodedChatbot.Database.Context.Interfaces;
using CoreCodedChatbot.Database.Context.Models;
using CoreCodedChatbot.Library.Interfaces.Services;
using Microsoft.Extensions.Logging;

namespace CoreCodedChatbot.Library.Services
{
    public class StreamStatusService : IStreamStatusService
    {
        private readonly IChatbotContextFactory _chatbotContextFactory;
        private readonly ILogger<IStreamStatusService> _logger;

        public StreamStatusService(
            IChatbotContextFactory chatbotContextFactory,
            ILogger<IStreamStatusService> logger)
        {
            _chatbotContextFactory = chatbotContextFactory;
            _logger = logger;
        }

        public bool GetStreamStatus(string broadcasterUsername)
        {
            using (var context = _chatbotContextFactory.Create())
            {
                var status = context.StreamStatuses.FirstOrDefault(s =>
                    s.BroadcasterUsername == broadcasterUsername);

                return status?.IsOnline ?? false;
            }
        }

        public bool SaveStreamStatus(PutStreamStatusRequest putStreamStatusRequest)
        {
            try
            {
                using (var context = _chatbotContextFactory.Create())
                {
                    var currentStatus = context.StreamStatuses.FirstOrDefault(s =>
                        s.BroadcasterUsername == putStreamStatusRequest.BroadcasterUsername);

                    if (currentStatus == null)
                    {
                        currentStatus = new StreamStatus
                        {
                            BroadcasterUsername = putStreamStatusRequest.BroadcasterUsername,
                            IsOnline = putStreamStatusRequest.IsOnline
                        };

                        context.StreamStatuses.Add(currentStatus);
                        context.SaveChanges();
                        return true;
                    }

                    currentStatus.IsOnline = putStreamStatusRequest.IsOnline;
                    context.SaveChanges();
                    return true;
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e,
                    $"Exception caught when saving Stream Status. broadcasterUsername: {putStreamStatusRequest.BroadcasterUsername}, isOnline: {putStreamStatusRequest.IsOnline}");

                return false;
            }
        }
    }
}