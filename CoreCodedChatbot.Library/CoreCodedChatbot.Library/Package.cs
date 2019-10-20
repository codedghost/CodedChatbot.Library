using System;
using System.Collections.Generic;
using System.Text;
using CoreCodedChatbot.Library.Interfaces.Services;
using CoreCodedChatbot.Library.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CoreCodedChatbot.Library
{
    public static class Package
    {
        public static ServiceCollection AddLibraryServices(this ServiceCollection services)
        {
            services.AddTransient<IConfigService, ConfigService>();
            
            services.AddSingleton<IGuessingGameService, GuessingGameService>();
            services.AddSingleton<IPlaylistService, PlaylistService>();
            services.AddSingleton<IStreamStatusService, StreamStatusService>();
            services.AddSingleton<IVipService, VipService>();

            return services;
        }
    }
}
