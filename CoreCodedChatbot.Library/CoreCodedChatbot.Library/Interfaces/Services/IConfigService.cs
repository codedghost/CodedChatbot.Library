using CoreCodedChatbot.Library.Models.Data;

namespace CoreCodedChatbot.Library.Interfaces.Services
{
    public interface IConfigService
    {
        T Get<T>(string configKey);
    }
}
