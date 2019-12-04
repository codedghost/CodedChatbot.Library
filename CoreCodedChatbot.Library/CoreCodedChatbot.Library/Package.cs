using System;
using System.Collections.Generic;
using System.Text;
using CoreCodedChatbot.Database;
using CoreCodedChatbot.Library.Interfaces.Services;
using CoreCodedChatbot.Library.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CoreCodedChatbot.Library
{
    public static class Package
    {
        public static IServiceCollection AddLibraryServices(this IServiceCollection services)
        {
            services.AddDbContextFactory();
            
            services.AddSingleton<IGuessingGameService, GuessingGameService>();
            services.AddSingleton<IPlaylistService, PlaylistService>();
            services.AddSingleton<IStreamStatusService, StreamStatusService>();
            services.AddSingleton<IVipService, VipService>();
            services.AddSingleton<ISignalRService, SignalRService>();

            return services;
        }
    }
}
