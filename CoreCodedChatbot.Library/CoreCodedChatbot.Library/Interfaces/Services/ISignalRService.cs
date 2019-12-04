using Microsoft.AspNetCore.SignalR.Client;

namespace CoreCodedChatbot.Library.Interfaces.Services
{
    public interface ISignalRService
    {
        HubConnection GetCurrentConnection();
    }
}