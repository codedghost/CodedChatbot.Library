using CoreCodedChatbot.ApiContract.RequestModels.StreamStatus;

namespace CoreCodedChatbot.Library.Interfaces.Services
{
    public interface IStreamStatusService
    {
        bool GetStreamStatus(string broadcasterUsername);
        bool SaveStreamStatus(PutStreamStatusRequest putStreamStatusRequest);
    }
}