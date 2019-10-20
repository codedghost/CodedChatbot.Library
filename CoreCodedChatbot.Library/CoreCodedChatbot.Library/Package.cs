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
            // TODO: Add a package class to library to do this.
            // Register Transient types
            services.AddTransient<IConfigService, ConfigService>();

            return services;
        }
    }
}
