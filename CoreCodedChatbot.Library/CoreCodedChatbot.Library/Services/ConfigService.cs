using System;
using System.IO;
using CoreCodedChatbot.Library.Interfaces.Services;
using CoreCodedChatbot.Library.Models.Data;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;

namespace CoreCodedChatbot.Library.Services
{
    public class ConfigService : IConfigService
    {
        private IConfigurationRoot _configRoot;

        public ConfigService()
        {
            var builder = new ConfigurationBuilder()
                .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")}.json", true, true)
                .AddEnvironmentVariables();

            _configRoot = builder.Build();
        }

        public T Get<T>(string configKey)
        {
            var configString = _configRoot[$"AppSettings:{configKey}"];

            if (string.IsNullOrWhiteSpace(configString))
                return default(T);

            return (T)Convert.ChangeType(configString, typeof(T));
        }
    }
}
